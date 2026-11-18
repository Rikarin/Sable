using System.Text;

namespace Sable.Driver.Build;

/// <summary>
///     A unit's <c>.sbi</c> file (docs/05 §5.4): what other units can see of
///     it — for each file its interface fingerprint, its extensions'
///     fingerprint, the dotted names it writes, and its source with every
///     body that is not interface written <c>{ … }</c> — written when the
///     unit's interface is first worked out, and read back by later builds
///     instead of walking the unit's files again.
/// </summary>
/// <remarks>
///     <para>
///         The format is text, with a version on its first line:
///         <code>
///         sable-interface 2
///         unit lib shapes-0.1.0
///         file shapes/src/lib.sb 3f2a… 9c01…
///         refs Sable.IO Shapes.Shape
///         --- shapes/src/lib.sb
///         module Shapes
///         public fn area(_ side: Int) -> Int { … }
///         </code>
///         It is kept in the build cache under a key over the unit's
///         content, which of the types its extensions extend the program
///         declares concretely, and the build's settings, so a file found is
///         only ever the one this build would write; one that cannot be read
///         is worked out again.
///     </para>
/// </remarks>
public sealed record InterfaceFile(string Unit, IReadOnlyList<(string File, FileInterface Interface)> Files) {
    public const string Header = "sable-interface 2";

    /// <summary>The file's text.</summary>
    public string Write() {
        var text = new StringBuilder(Header).Append('\n').Append("unit ").Append(Unit).Append('\n');
        foreach (var (file, face) in Files) {
            text.Append("file ").Append(file).Append(' ').Append(face.Fingerprint).Append(' ').Append(face.Extensions).Append('\n');
            text.Append("refs");
            foreach (var reference in face.References) {
                text.Append(' ').Append(reference);
            }

            text.Append('\n');
        }

        foreach (var (file, face) in Files) {
            text.Append("--- ").Append(file).Append('\n').Append(face.Source);
            if (!face.Source.EndsWith('\n')) {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    ///     The interfaces a <c>.sbi</c> file gives the unit's files, in order;
    ///     null unless it is whole, of this version and of exactly these files.
    /// </summary>
    public static IReadOnlyList<FileInterface>? Read(string path, string unit, IReadOnlyList<string> files) {
        try {
            if (!File.Exists(path)) {
                return null;
            }

            using var reader = new StreamReader(path);
            if (reader.ReadLine() != Header || reader.ReadLine() != "unit " + unit) {
                return null;
            }

            var heads = new List<(string Fingerprint, string Extensions, string[] References)>();
            foreach (var file in files) {
                var prefix = "file " + file + " ";
                if (reader.ReadLine() is not { } line || !line.StartsWith(prefix, StringComparison.Ordinal) || line.Length != prefix.Length + 64 + 1 + 64
                    || reader.ReadLine() is not { } refs || !(refs == "refs" || refs.StartsWith("refs ", StringComparison.Ordinal))) {
                    return null;
                }

                heads.Add((line.Substring(prefix.Length, 64), line[^64..], refs.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..]));
            }

            // The sources follow, one section per file: a file cut short was not written whole.
            var sources = new List<StringBuilder>();
            while (reader.ReadLine() is { } line) {
                if (sources.Count < files.Count && line == "--- " + files[sources.Count]) {
                    sources.Add(new());
                } else if (sources.Count > 0) {
                    sources[^1].Append(line).Append('\n');
                }
            }

            return sources.Count == files.Count
                ? [..heads.Select((h, i) => new FileInterface(h.Fingerprint, sources[i].ToString(), h.Extensions, h.References))]
                : null;
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
