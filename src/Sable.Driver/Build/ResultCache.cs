using System.Text;
using System.Text.Json;
using Sable.Syntax;

namespace Sable.Driver.Build;

/// <summary>
///     What <c>sable check</c>, <c>test</c> and <c>bench</c> worked out from a
///     program, kept under a key over everything they read — the compiler, the
///     settings, and each file's name, text and the options it was parsed
///     with — so that running one again on unchanged sources reads the answer
///     instead of type-checking or compiling the program again (docs/05 §5.4).
/// </summary>
/// <remarks>
///     As with the build cache, the key is content, never a timestamp, and
///     an entry is written whole or not at all; one that cannot be read is
///     a miss.
/// </remarks>
public static class ResultCache {
    /// <summary>Format version: bump when what is kept, or how it is keyed, changes meaning.</summary>
    const string Version = "sable-result-cache-1";

    /// <summary>The key of a program's result under <paramref name="settings" />.</summary>
    public static string Key(IEnumerable<SyntaxTree> program, string settings) {
        var text = new StringBuilder(Version).Append('\n').Append(IncrementalBuild.Toolchain).Append('\n').Append(settings).Append('\n');
        foreach (var tree in program) {
            var options = tree.Options;
            text.Append(tree.Text.FileName).Append('\n')
                .Append(string.Join(",", options.Symbols.Order(StringComparer.Ordinal))).Append('|')
                .Append(string.Join(",", options.Features.Order(StringComparer.Ordinal))).Append('|')
                .Append(options.TargetOs).Append('/').Append(options.TargetArch).Append('|')
                .Append(options.Workspace).Append('\n')
                .Append(IncrementalBuild.Hash(tree.Text.Text)).Append('\n');
        }

        return IncrementalBuild.Hash(text.ToString());
    }

    sealed record Entry(string Key, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    /// <summary>The errors and warnings kept in <paramref name="file" /> under <paramref name="key" />, or null.</summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)? Read(string file, string key) {
        try {
            return File.Exists(file) && JsonSerializer.Deserialize<Entry>(File.ReadAllText(file)) is { } entry && entry.Key == key
                ? (entry.Errors, entry.Warnings)
                : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    /// <summary>Keeps the errors and warnings in <paramref name="file" /> under <paramref name="key" />.</summary>
    public static void Write(string file, string key, IReadOnlyList<string> errors, IReadOnlyList<string> warnings) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry(key, errors, warnings)));
            File.Move(temporary, file, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // A cache that cannot be written is only slower next time.
        }
    }

    /// <summary>A name for a file in the cache, from anything.</summary>
    public static string FileName(string name) => string.Concat(name.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
