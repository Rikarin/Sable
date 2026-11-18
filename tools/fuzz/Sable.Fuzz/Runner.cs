using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sable.Fuzz;

/// <summary>
///     Runs a target on one input at a time, on a thread of its own with the
///     8 MB stack a process's main thread gets (so a recursion too deep here is
///     too deep for <c>sable</c>), and stops waiting at the target's limit: a
///     hang is reported, not waited out. A hung input's thread cannot be
///     stopped, so after a hang the process has to end.
/// </summary>
public sealed partial class Runner : IDisposable {
    readonly Target _target;
    readonly Thread _thread;
    readonly SemaphoreSlim _go = new(0);
    readonly SemaphoreSlim _done = new(0);
    byte[] _input = [];
    Exception? _failure;
    volatile bool _closing;

    public Runner(Target target) {
        _target = target;
        _thread = new Thread(Work, 8 * 1024 * 1024) { IsBackground = true, Name = "fuzz input" };
        _thread.Start();
    }

    /// <summary>Set from a signal handler: whatever is waiting gives up at once.</summary>
    public static volatile bool Stopping;

    void Work() {
        while (true) {
            _go.Wait();
            if (_closing) {
                return;
            }

            _failure = null;
            try {
                _target.Run(_input);
            } catch (Exception e) {
                _failure = e;
            }

            _done.Release();
        }
    }

    public enum Outcome { Passed, Failed, Hung, Stopped }

    /// <summary>One input through the target: how it went, and what it threw if it failed.</summary>
    public (Outcome Outcome, Exception? Failure) Run(byte[] input, TimeSpan? limit = null) {
        if (Stopping) {
            return (Outcome.Stopped, null);
        }

        _input = input;
        _go.Release();
        var deadline = DateTime.UtcNow + (limit ?? _target.Limit);
        while (!_done.Wait(100)) {
            if (Stopping) {
                return (Outcome.Stopped, null);
            }

            if (DateTime.UtcNow > deadline) {
                return (Outcome.Hung, null);
            }
        }

        return _failure is null ? (Outcome.Passed, null) : (Outcome.Failed, _failure);
    }

    public void Dispose() {
        _closing = true;
        _go.Release();
    }

    /// <summary>
    ///     What makes two failures the same one: what was thrown, and the
    ///     compiler's own frame nearest the throw — not the message, which
    ///     names what the input happened to hold (a duplicate key's name is
    ///     not another bug) — or, for a promise broken, the promise, with its
    ///     numbers and quoted text taken out. With no frame of the compiler's,
    ///     the message stands in for one.
    /// </summary>
    public static string Signature(Exception failure) {
        var type = failure.GetType().Name;
        var said = Normalise(failure.Message.Split('\n')[0]);
        var frame = (failure.StackTrace ?? "").Split('\n').Select(static l => l.Trim())
            .FirstOrDefault(static l => l.StartsWith("at Sable.", StringComparison.Ordinal) && !l.StartsWith("at Sable.Fuzz.", StringComparison.Ordinal)) ?? "";
        var paren = frame.IndexOf('(');
        frame = (paren < 0 ? frame : frame[..paren]).Replace("at ", "", StringComparison.Ordinal);
        return failure is BrokenPromise ? said : frame.Length > 0 ? $"{type} at {frame}" : $"{type}: {said}";
    }

    static string Normalise(string text) {
        text = Quoted().Replace(text, "'…'");
        text = Number().Replace(text, "N");
        return text.Length > 160 ? text[..160] : text;
    }

    [GeneratedRegex("'[^']*'|\"[^\"]*\"")]
    private static partial Regex Quoted();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Number();

    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    /// <summary>
    ///     Delta debugging over bytes — whole lines first, then ever smaller
    ///     chunks — to the smallest input that still fails the same way, so a
    ///     report is a repro a person can read. Gives up improving at the
    ///     deadline and returns the best so far.
    /// </summary>
    public byte[] Minimise(byte[] input, string signature, TimeSpan budget) {
        var deadline = DateTime.UtcNow + budget;

        bool StillFails(byte[] candidate) =>
            Run(candidate) is (Outcome.Failed, { } f) && Signature(f) == signature;

        var current = input;

        // Lines: a source's natural unit.
        var lines = Lines(current);
        for (var chunk = Math.Max(1, lines.Count / 2); chunk >= 1 && DateTime.UtcNow < deadline; chunk /= 2) {
            for (var at = 0; at < lines.Count && DateTime.UtcNow < deadline;) {
                var candidate = new List<byte[]>(lines);
                candidate.RemoveRange(at, Math.Min(chunk, lines.Count - at));
                var bytes = candidate.SelectMany(static l => l).ToArray();
                if (candidate.Count > 0 && StillFails(bytes)) {
                    lines = candidate;
                    current = bytes;
                } else {
                    at += chunk;
                }
            }
        }

        for (var chunk = Math.Max(1, current.Length / 2); chunk >= 1 && DateTime.UtcNow < deadline; chunk /= 2) {
            for (var at = 0; at < current.Length && DateTime.UtcNow < deadline;) {
                var candidate = current[..at].Concat(current[Math.Min(current.Length, at + chunk)..]).ToArray();
                if (candidate.Length > 0 && StillFails(candidate)) {
                    current = candidate;
                } else {
                    at += chunk;
                }
            }
        }

        return current;
    }

    static List<byte[]> Lines(byte[] data) {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < data.Length; i++) {
            if (data[i] == '\n') {
                lines.Add(data[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < data.Length) {
            lines.Add(data[start..]);
        }

        return lines;
    }

    /// <summary>A failure as a person reads it: what was thrown, where, and the input as text.</summary>
    public static string Report(Exception failure, byte[] input) {
        var builder = new StringBuilder();
        builder.Append("signature: ").Append(Signature(failure)).Append('\n');
        builder.Append(failure).Append("\n\ninput (").Append(input.Length).Append(" bytes, as UTF-8):\n");
        builder.Append(Encoding.UTF8.GetString(input)).Append('\n');
        return builder.ToString();
    }
}
