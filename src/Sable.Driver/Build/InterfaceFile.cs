using System.Text;

namespace Sable.Driver.Build;

/// <summary>
///     A unit's <c>.sbi</c> file (docs/05 §5.4): what other units can see of
///     it — each file's interface fingerprint, and its source with every
///     body that is not interface written <c>{ … }</c> — written when the
///     unit's interface is first worked out, and read back by later builds
///     instead of walking the unit's files again.
/// </summary>
/// <remarks>
///     <para>
///         The format is text, with a version on its first line:
///         <code>
///         sable-interface 1
///         unit lib shapes-0.1.0
///         file shapes/src/lib.sb 3f2a…
///         --- shapes/src/lib.sb
///         module Shapes
///         public fn area(_ side: Int) -> Int { … }
///         </code>
///         It is kept in the build cache under a key over the unit's
///         content, the program's concrete types (which decide which
///         extensions' bodies are interface) and the build's settings, so a
///         file found is only ever the one this build would write; one that
///         cannot be read is worked out again.
///     </para>
///     <para>
///         Not yet what docs/05 plans: a binary format with escape summaries,
///         layouts and conformance records that another compilation binds
///         against in place of the source, and that a unit's dependents alone
///         are keyed by. A unit's lowering still reads facts of the whole
///         program — which classes anything inherits from decides whether a
///         method call is dispatched — so every unit is keyed by the whole
///         program's interface.
///     </para>
/// </remarks>
public sealed record InterfaceFile(string Unit, IReadOnlyList<(string File, string Fingerprint)> Files, IReadOnlyList<string> Sources) {
    public const string Header = "sable-interface 1";

    /// <summary>The file's text.</summary>
    public string Write() {
        var text = new StringBuilder(Header).Append('\n').Append("unit ").Append(Unit).Append('\n');
        foreach (var (file, fingerprint) in Files) {
            text.Append("file ").Append(file).Append(' ').Append(fingerprint).Append('\n');
        }

        for (var i = 0; i < Files.Count; i++) {
            text.Append("--- ").Append(Files[i].File).Append('\n').Append(Sources[i]);
            if (!Sources[i].EndsWith('\n')) {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    ///     The fingerprints a <c>.sbi</c> file gives the unit's files, in order;
    ///     null unless it is whole, of this version and of exactly these files.
    /// </summary>
    public static IReadOnlyList<string>? ReadFingerprints(string path, string unit, IReadOnlyList<string> files) {
        try {
            if (!File.Exists(path)) {
                return null;
            }

            using var reader = new StreamReader(path);
            if (reader.ReadLine() != Header || reader.ReadLine() != "unit " + unit) {
                return null;
            }

            var fingerprints = new List<string>();
            foreach (var file in files) {
                var prefix = "file " + file + " ";
                if (reader.ReadLine() is not { } line || !line.StartsWith(prefix, StringComparison.Ordinal) || line.Length != prefix.Length + 64) {
                    return null;
                }

                fingerprints.Add(line[prefix.Length..]);
            }

            // The sources follow, one section per file: a file cut short was not written whole.
            var sections = 0;
            while (reader.ReadLine() is { } line) {
                if (sections < files.Count && line == "--- " + files[sections]) {
                    sections++;
                }
            }

            return sections == files.Count ? fingerprints : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Writes the file whole or not at all.</summary>
    public void WriteTo(string path) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, Write());
            File.Move(temporary, path, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Not kept: the next build works the interface out again.
        }
    }
}
