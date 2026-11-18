using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Completion latency of `sable lsp` on a generated 200,000-line project
// (docs/04 Phase 5's exit criterion: < 50 ms at p95).
//
//   dotnet run -c Release --project tools/Sable.LspBench [-- --files 180 --sites 24]
//
// It writes a multi-file, multi-module project to a temporary folder,
// starts `sable lsp` as a separate process, as an editor does, opens one
// file and types statements into function bodies a character at a time,
// asking for completion after every keystroke, at typing speed, with
// pauses long enough for the server to rebuild its compilation in the
// background meanwhile. Latency is measured at the client: from writing the
// request to reading its response, so framing, JSON and the pipe are in it.

var files = Argument("--files", 180);
var sites = Argument("--sites", 24);
var seed = Argument("--seed", 1);
var keep = args.Contains("--keep");

var root = Path.Combine(Path.GetTempPath(), "sable-lsp-bench", Guid.NewGuid().ToString("N"));
var lines = Generate(root, files);
Console.WriteLine($"project: {files} files, {lines:N0} lines, in {root}");

var sable = Path.Combine(AppContext.BaseDirectory, "sable.dll");
using var server = Process.Start(new ProcessStartInfo("dotnet", [sable, "lsp"]) {
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
})!;
server.ErrorDataReceived += static (_, e) => {
    if (e.Data is { Length: > 0 } line) {
        Console.Error.WriteLine(line);
    }
};
server.BeginErrorReadLine();

var client = new Client(server.StandardOutput.BaseStream, server.StandardInput.BaseStream);
var watch = Stopwatch.StartNew();
await client.RequestAsync("initialize", new JsonObject {
    ["processId"] = Environment.ProcessId,
    ["rootUri"] = new Uri(root).AbsoluteUri,
    ["capabilities"] = new JsonObject()
});
await client.NotifyAsync("initialized", new JsonObject());

// A file in the middle of the project, open in the editor.
var path = Path.Combine(root, $"part{files / 2 / 20:D2}", $"file{files / 2:D3}.sb");
var uri = new Uri(path).AbsoluteUri;
var text = File.ReadAllText(path);
await client.NotifyAsync("textDocument/didOpen", new JsonObject {
    ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "sable", ["version"] = 1, ["text"] = text }
});
var firstDiagnostics = await client.DiagnosticsAsync(uri);
var publishedBefore = client.Published;
Console.WriteLine($"first diagnostics: {watch.Elapsed.TotalMilliseconds:F0} ms after start ({firstDiagnostics} in the open file)");

// Statements typed into function bodies: a member access, a call, locals.
var random = new Random(seed);
var version = 1;
var all = new List<double>();
var afterDot = new List<double>();
var empty = 0;
var missing = 0;
var bodies = BodyStarts(text);
for (var site = 0; site <= sites; site++) {
    var warmUp = site == 0;
    var at = bodies[(site * 7919) % bodies.Count];

    // Re-find the place: earlier sites made the text longer.
    at = BodyStarts(text)[(site * 7919) % bodies.Count];
    var statement = $"let probe{site} = other.total(mine + theirs)\n        ";
    for (var i = 0; i < statement.Length; i++) {
        var typed = statement[i].ToString();
        var position = Position(text, at + i);
        version++;
        await client.NotifyAsync("textDocument/didChange", new JsonObject {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject {
                ["range"] = new JsonObject { ["start"] = position.DeepClone(), ["end"] = position.DeepClone() },
                ["text"] = typed
            })
        });
        text = text.Insert(at + i, typed);

        if (char.IsWhiteSpace(statement[i])) {
            continue;
        }

        var caret = Position(text, at + i + 1);
        var timer = Stopwatch.StartNew();
        var result = await client.RequestAsync("textDocument/completion", new JsonObject {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = caret
        });
        var elapsed = timer.Elapsed.TotalMilliseconds;

        var labels = result?["items"]?.AsArray().Select(static item => item!["label"]!.GetValue<string>()).ToList() ?? [];
        if (labels.Count == 0) {
            empty++;
        }

        var dot = statement[..(i + 1)].EndsWith("other.", StringComparison.Ordinal);
        if (dot && !labels.Contains("total")) {
            missing++;
        }

        if (!warmUp) {
            all.Add(elapsed);
            if (dot) {
                afterDot.Add(elapsed);
            }
        }

        // Typing speed: 40 to 120 ms between keys.
        await Task.Delay(40 + random.Next(80));
    }

    // A pause: long enough for the server to rebuild in the background,
    // which the next site's first keystrokes then race.
    await Task.Delay(400 + random.Next(600));
}

Console.WriteLine();
Console.WriteLine($"completion, {all.Count} requests after a warm-up site ({empty} empty, {missing} member lists missing 'total'),");
Console.WriteLine($"while the server rebuilt its compilation {client.Published - publishedBefore} times in the background:");
Report("all", all);
Report("after '.'", afterDot);

// For contrast: a hover right after an edit asks about declarations, so it
// waits for a compilation of the edited workspace — the path a completion
// takes only when the member it is in is new.
var hovers = new List<double>();
for (var i = 0; i < 6; i++) {
    var at = BodyStarts(text)[i];
    var position = Position(text, at);
    version++;
    await client.NotifyAsync("textDocument/didChange", new JsonObject {
        ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
        ["contentChanges"] = new JsonArray(new JsonObject {
            ["range"] = new JsonObject { ["start"] = position.DeepClone(), ["end"] = position.DeepClone() },
            ["text"] = " "
        })
    });
    text = text.Insert(at, " ");
    var timer = Stopwatch.StartNew();
    await client.RequestAsync("textDocument/hover", new JsonObject {
        ["textDocument"] = new JsonObject { ["uri"] = uri },
        ["position"] = Position(text, text.IndexOf("other.total(2)", at, StringComparison.Ordinal) + 7)
    });
    hovers.Add(timer.Elapsed.TotalMilliseconds);
    await Task.Delay(1500);
}

Console.WriteLine("hover just after an edit, which waits for a new compilation:");
Report("hover", hovers);

await client.RequestAsync("shutdown", null);
await client.NotifyAsync("exit", null);
await server.WaitForExitAsync();
if (!keep) {
    Directory.Delete(root, true);
}

return all.Count > 0 && Percentile(all, 95) < 50 ? 0 : 1;

static void Report(string name, List<double> samples) {
    if (samples.Count == 0) {
        return;
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  {name,-10} n={samples.Count,4}  p50 {Percentile(samples, 50),6:F1} ms  p95 {Percentile(samples, 95),6:F1} ms  p99 {Percentile(samples, 99),6:F1} ms  max {samples.Max(),6:F1} ms"));
}

static double Percentile(List<double> samples, double p) {
    var sorted = samples.Order().ToList();
    var rank = (int)Math.Ceiling(p / 100 * sorted.Count) - 1;
    return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
}

int Argument(string name, int fallback) {
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1], CultureInfo.InvariantCulture) : fallback;
}

static JsonObject Position(string text, int offset) {
    var line = 0;
    var start = 0;
    for (var i = 0; i < offset; i++) {
        if (text[i] == '\n') {
            line++;
            start = i + 1;
        }
    }

    return new JsonObject { ["line"] = line, ["character"] = offset - start };
}

// Where a statement can be typed: the start of the line after 'let mine' in each 'combined'.
static List<int> BodyStarts(string text) {
    var starts = new List<int>();
    const string marker = "        let theirs = other.total(2)\n";
    for (var at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = text.IndexOf(marker, at + 1, StringComparison.Ordinal)) {
        starts.Add(at + marker.Length + 8);
    }

    return starts;
}

/// <summary>
///     A project of <paramref name="files" /> files in modules of twenty,
///     each module importing the one before it; every type refers to one in
///     another file. About a thousand lines a file.
/// </summary>
static int Generate(string root, int files) {
    var total = 0;
    for (var f = 0; f < files; f++) {
        var module = f / 20;
        var folder = Path.Combine(root, $"part{module:D2}");
        Directory.CreateDirectory(folder);

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"module Bench.Part{module}\n");
        if (module > 0) {
            builder.Append(CultureInfo.InvariantCulture, $"import Bench.Part{module - 1}\n");
        }

        builder.Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $$"""
            /// What a file's items are made of.
            public union Shape{{f}} {
                Circle(radius: Int)
                Rect(width: Int, height: Int)
                Empty
            }

            public fn area{{f}}(_ shape: Shape{{f}}) -> Int {
                return match shape {
                    case .Circle(let r) => 3 * r * r
                    case .Rect(let w, let h) => w * h
                    case .Empty => 0
                }
            }


            """);

        for (var t = 0; t < 34; t++) {
            var index = f * 100 + t;
            var other = f > 0 ? $"Item{(f - 1) * 100 + t}" : $"Item{index}";
            builder.Append(CultureInfo.InvariantCulture, $$"""
                /// Item {{index}}: a record with a few methods.
                public struct Item{{index}} {
                    public var id: Int
                    public var name: String

                    public fn describe() -> String {
                        return "item \(id): \(name)"
                    }

                    public fn total(_ quantity: Int) -> Int {
                        var sum = 0
                        var k = 0
                        while k < quantity {
                            sum = sum + k * {{t + 1}}
                            k = k + 1
                        }
                        return sum
                    }

                    public fn combined(_ other: {{other}}) -> Int {
                        let mine = total(1)
                        let theirs = other.total(2)
                        if mine > theirs {
                            return mine - theirs + id
                        }
                        return mine + theirs + id + other.id
                    }

                    public fn shape() -> Shape{{f}} {
                        return id > 0 ? Shape{{f}}.Rect(width: id, height: 2) : Shape{{f}}.Empty
                    }
                }


                """);
        }

        var text = builder.ToString();
        total += text.Count(static c => c == '\n');
        File.WriteAllText(Path.Combine(folder, $"file{f:D3}.sb"), text);
    }

    return total;
}

/// <summary>Just enough of an editor: framed JSON-RPC, requests by id, diagnostics noticed.</summary>
sealed class Client {
    readonly Stream _input;
    readonly Stream _output;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _waiting = new();
    readonly ConcurrentDictionary<string, int> _diagnostics = new();
    int _published;

    /// <summary>How many times the server has published diagnostics: each is a compilation it rebuilt.</summary>
    public int Published => Volatile.Read(ref _published);
    readonly SemaphoreSlim _write = new(1, 1);
    int _next;

    public Client(Stream input, Stream output) {
        _input = input;
        _output = output;
        _ = Task.Run(ReadAsync);
    }

    async Task ReadAsync() {
        while (true) {
            var length = 0;
            while (true) {
                var line = await ReadLineAsync();
                if (line is null) {
                    return;
                }

                if (line.Length == 0) {
                    break;
                }

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                    length = int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            var body = new byte[length];
            await _input.ReadExactlyAsync(body);
            var message = JsonNode.Parse(body)!;
            if (message["id"] is { } id && message["method"] is null) {
                if (_waiting.TryRemove(id.GetValue<int>(), out var waiting)) {
                    if (message["error"] is { } error) {
                        waiting.SetException(new InvalidOperationException(error.ToJsonString()));
                    } else {
                        waiting.SetResult(message["result"]);
                    }
                }
            } else if (message["method"]?.GetValue<string>() == "textDocument/publishDiagnostics") {
                var parameters = message["params"]!;
                _diagnostics[parameters["uri"]!.GetValue<string>()] = parameters["diagnostics"]!.AsArray().Count;
                Interlocked.Increment(ref _published);
            }
        }
    }

    readonly byte[] _one = new byte[1];

    async Task<string?> ReadLineAsync() {
        var bytes = new List<byte>();
        while (true) {
            if (await _input.ReadAsync(_one) == 0) {
                return null;
            }

            if (_one[0] == '\n') {
                return Encoding.ASCII.GetString([..bytes]).TrimEnd('\r');
            }

            bytes.Add(_one[0]);
        }
    }

    async Task WriteAsync(JsonObject message) {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _write.WaitAsync();
        try {
            await _output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await _output.WriteAsync(body);
            await _output.FlushAsync();
        } finally {
            _write.Release();
        }
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters) {
        var id = Interlocked.Increment(ref _next);
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiting;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        return await waiting.Task;
    }

    public Task NotifyAsync(string method, JsonNode? parameters) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    public async Task<int> DiagnosticsAsync(string uri) {
        while (!_diagnostics.ContainsKey(uri)) {
            await Task.Delay(20);
        }

        return _diagnostics[uri];
    }
}
