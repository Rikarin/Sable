using System.Text;
using Sable.Driver.Commands;

namespace Sable.Driver;

/// <summary>
///     The inputs <c>sable fuzz</c> kept, listed for <c>sable test</c> to run
///     each once (Sable.Testing's <c>fuzzReplays</c>): a file for each target,
///     a line an input.
/// </summary>
public static class FuzzReplays {
    /// <summary>The environment variable that names the directory of the lists.</summary>
    public const string Variable = "SABLE_FUZZ_INPUTS";

    /// <summary>How much of one target is listed: its failures, then its corpus from the smallest. 'sable fuzz --replay' runs it all.</summary>
    const int MostInputs = 2000;

    const long MostBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     Lists the saved inputs of every target that has any: what failed
    ///     and the corpus, under <paramref name="targetDirectory" />, and the
    ///     seeds under the program's <c>fuzz/</c>. Gives the directory of the
    ///     lists, which the caller deletes; null when there are none.
    /// </summary>
    public static string? List(string directory, string targetDirectory) {
        string? lists = null;
        foreach (var target in new[] { Path.Combine(directory, "fuzz"), Path.Combine(targetDirectory, "fuzz") }
                     .Where(Directory.Exists).SelectMany(Directory.GetDirectories).Select(Path.GetFileName).OfType<string>()
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            var (crashes, corpus) = FuzzCommand.SavedInputs(directory, targetDirectory, target);
            var text = new StringBuilder();
            var listed = 0;
            foreach (var (kind, path) in crashes.Select(static p => ("crash", p)).Concat(corpus.OrderBy(static p => new FileInfo(p).Length).Select(static p => ("corpus", p)))) {
                var bytes = File.ReadAllBytes(path);
                if (listed == MostInputs || text.Length / 2 + bytes.Length > MostBytes) {
                    break;
                }

                text.Append(kind).Append('\t').Append(path.Replace('\\', '/')).Append('\t').Append(Convert.ToHexStringLower(bytes)).Append('\n');
                listed++;
            }

            if (listed > 0) {
                lists ??= Directory.CreateTempSubdirectory("sable-replay-").FullName;
                File.WriteAllText(Path.Combine(lists, target + ".inputs"), text.ToString());
            }
        }

        return lists;
    }
}
