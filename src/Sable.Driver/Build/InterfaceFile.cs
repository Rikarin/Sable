using System.Security.Cryptography;
using System.Text;

namespace Sable.Driver.Build;

/// <summary>
///     A unit's <c>.sbi</c> file (docs/05 §5.4): what other units bind and
///     lower against — for each file its interface fingerprint, its
///     extensions' fingerprint, the dotted names it writes, and its source
///     with every body that is not interface blanked — written when the
///     unit's interface is first worked out, and read back by later builds:
///     an unchanged unit's interface is not worked out again, and a unit
///     whose object is kept is parsed and bound from it rather than from its
///     source.
/// </summary>
/// <remarks>
///     <para>
///         The file is binary: the bytes <c>SBI</c>, a version, the payload,
///         and the payload's SHA-256. One of another version, cut short, or
///         whose hash does not match is not read, and is worked out again.
///         In the payload, strings are length-prefixed UTF-8 and a source is
///         a list of pieces of text, each followed by a count of spaces —
///         a blanked body, or indentation — so that what other units do not
///         see takes almost no room.
///     </para>
///     <para>
///         It is kept in the build cache under a key over the unit's content
///         and the build's settings. What else the interface depends on —
///         which of the types its extensions extend the program declares as
///         concrete types (<see cref="InterfaceFingerprint.ConcreteTypeNames" />),
///         which decides whether their bodies are interface — is written
///         in it (<see cref="Assumed" />), and a build that finds the program
///         otherwise works the interface out again.
///     </para>
/// </remarks>
public sealed record InterfaceFile(string Unit, IReadOnlyList<string> Assumed, IReadOnlyList<(string File, FileInterface Interface)> Files) {
    /// <summary>The format's version: bump when what is written, or what it means, changes.</summary>
    public const int Version = 3;

    static ReadOnlySpan<byte> Magic => "SBI"u8;

    /// <summary>Indentation and blanked bodies: runs of at least this many spaces are counted, not written.</summary>
    const int Run = 4;

    /// <summary>The file's bytes.</summary>
    public byte[] Write() {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true)) {
            writer.Write(Unit);
            WriteList(writer, Assumed);
            writer.Write(Files.Count);
            foreach (var (file, face) in Files) {
                writer.Write(file);
                writer.Write(face.Fingerprint);
                writer.Write(face.Extensions);
                WriteList(writer, face.References);
                WriteSource(writer, face.Source);
            }
        }

        var body = payload.ToArray();
        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(BitConverter.GetBytes(Version));
        output.Write(body);
        output.Write(SHA256.HashData(body));
        return output.ToArray();
    }

    static void WriteList(BinaryWriter writer, IReadOnlyCollection<string> items) {
        writer.Write(items.Count);
        foreach (var item in items) {
            writer.Write(item);
        }
    }

    static void WriteSource(BinaryWriter writer, string source) {
        var pieces = new List<(int Start, int Length, int Spaces)>();
        var start = 0;
        for (var i = 0; i < source.Length;) {
            if (source[i] != ' ') {
                i++;
                continue;
            }

            var end = i;
            while (end < source.Length && source[end] == ' ') {
                end++;
            }

            if (end - i >= Run) {
                pieces.Add((start, i - start, end - i));
                start = end;
            }

            i = end;
        }

        pieces.Add((start, source.Length - start, 0));
        writer.Write(pieces.Count);
        foreach (var (at, length, spaces) in pieces) {
            writer.Write(source.Substring(at, length));
            writer.Write(spaces);
        }
    }

    /// <summary>
    ///     What a <c>.sbi</c> file holds; null unless it is whole, of this
    ///     version, of this unit and of exactly these files.
    /// </summary>
    public static InterfaceFile? Read(string path, string unit, IReadOnlyList<string> files) {
        try {
            if (!File.Exists(path)) {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            const int header = 3 + sizeof(int), hash = 32;
            if (bytes.Length < header + hash || !bytes.AsSpan(0, 3).SequenceEqual(Magic) || BitConverter.ToInt32(bytes, 3) != Version) {
                return null;
            }

            var body = bytes.AsSpan(header, bytes.Length - header - hash);
            if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - hash))) {
                return null;
            }

            using var reader = new BinaryReader(new MemoryStream(bytes, header, body.Length), Encoding.UTF8);
            if (reader.ReadString() != unit) {
                return null;
            }

            var assumed = ReadList(reader);
            if (reader.ReadInt32() != files.Count) {
                return null;
            }

            var read = new List<(string, FileInterface)>();
            foreach (var file in files) {
                if (reader.ReadString() != file) {
                    return null;
                }

                var fingerprint = reader.ReadString();
                var extensions = reader.ReadString();
                var references = ReadList(reader);
                read.Add((file, new FileInterface(fingerprint, ReadSource(reader), extensions, references)));
            }

            return new(unit, assumed, read);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException or ArgumentException) {
            return null;
        }
    }

    static List<string> ReadList(BinaryReader reader) {
        var count = reader.ReadInt32();
        var items = new List<string>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++) {
            items.Add(reader.ReadString());
        }

        return items;
    }

    static string ReadSource(BinaryReader reader) {
        var count = reader.ReadInt32();
        var source = new StringBuilder();
        for (var i = 0; i < count; i++) {
            source.Append(reader.ReadString()).Append(' ', reader.ReadInt32());
        }

        return source.ToString();
    }

    /// <summary>Writes the file whole or not at all.</summary>
    public void WriteTo(string path) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, Write());
            File.Move(temporary, path, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Not kept: the next build works the interface out again.
        }
    }
}
