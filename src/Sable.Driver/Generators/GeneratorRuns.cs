using System.Text.Json;
using Sable.Syntax;

namespace Sable.Driver.Generators;

/// <summary>
///     What the library's generators gave for an input (docs/08 §8.6), kept
///     under <c>$SABLE_HOME/cache</c> with the compiler's other records of its
///     library (<see cref="Library.CheckedPath" />), which <c>sable cache</c>
///     prunes and cleans: a build with nothing new to generate neither
///     compiles the generators nor runs them.
/// </summary>
static class GeneratorRuns {
    /// <summary>A message a run reported, and where.</summary>
    internal sealed record Message(DiagnosticSeverity Severity, string Location, string Text);

    /// <summary>A file a run wrote.</summary>
    internal sealed record File(string Name, string Module, string Text);

    /// <summary>What a run gave.</summary>
    internal sealed record Output(IReadOnlyList<Message> Messages, IReadOnlyList<File> Files);

    /// <summary>How many are kept: past it, the older half goes.</summary>
    const int Limit = 1000;

    static string Directory => Path.Combine(Library.RecordsOf(Library.CheckedPath), "generated");

    /// <summary>What the run of this key gave, if it is kept.</summary>
    public static Output? Read(string key) {
        try {
            var path = Path.Combine(Directory, key + ".json");
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<Output>(System.IO.File.ReadAllText(path)) : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    /// <summary>Keeps what the run of this key gave.</summary>
    public static void Write(string key, Output output) {
        try {
            var directory = System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(directory.FullName, key + ".json");
            if (System.IO.File.Exists(path)) {
                return;
            }

            // Whole or not at all: another process may be reading it.
            var temporary = Path.Combine(directory.FullName, Path.GetRandomFileName());
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(output));
            System.IO.File.Move(temporary, path, overwrite: true);
            var kept = directory.GetFiles("*.json");
            if (kept.Length > Limit) {
                foreach (var old in kept.OrderBy(static f => f.LastWriteTimeUtc).Take(kept.Length / 2)) {
                    old.Delete();
                }
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Run again by the next process.
        }
    }
}
