using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     A library unit's interface, as the <c>.sbi</c> file a build keeps for
///     it (docs/05 §5.4, #182): what a program needs of the library to be
///     compiled against it without binding the library's bodies or lowering
///     its code. A program compiled against it (see the driver's
///     <c>Pipeline.CompileAgainst</c>) checks and lowers only its own files,
///     and takes the library's code from here.
/// </summary>
/// <remarks>
///     <para>
///         It holds the unit's bound symbols (<see cref="SymbolInterface" />)
///         and its SIL: every function of its files lowered as a unit of a
///         program compiled separately — what is not generic, with the
///         instances of generic functions it uses itself. A generic
///         function's other instances, an <c>@inline</c> body, a
///         <c>const fn</c> and a global's initial value are lowered by the
///         program from the library's source, which is the unit of
///         distribution and comes with the compiler or the package: SIL is
///         monomorphic until generics are compiled once (#297), so it cannot
///         hold a generic body for an instance it does not know. Which files
///         the source must be — each one's name and the SHA-256 of its text —
///         is recorded, and a program whose library files differ is not
///         compiled against it.
///     </para>
///     <para>
///         The file is binary: the bytes <c>SBIL</c>, the format's version,
///         the payload, and the payload's SHA-256. The payload starts with
///         the compiler's identity — a hash of its own build
///         (<see cref="IdentityOf" />) — and the settings the unit was
///         compiled with, so that an interface another build of the compiler
///         wrote, or one built for another target or profile, is refused, as
///         Rust's metadata is: the format has no stability promise across
///         compilers, and any mismatch rebuilds. Strings are length-prefixed
///         UTF-8; the SIL is its text, as <see cref="SilPrinter" /> writes it
///         and <see cref="SilParser" /> reads it, compressed with Brotli.
///     </para>
/// </remarks>
public sealed class LibraryInterface {
    /// <summary>The format's version: bump when what is written, or what it means, changes.</summary>
    public const int Version = 1;

    static ReadOnlySpan<byte> Magic => "SBIL"u8;

    LibraryInterface(
        string unit,
        string compiler,
        string settings,
        IReadOnlyList<(string File, string Hash)> sources,
        IReadOnlyList<string> modules,
        IReadOnlyList<string> extensions,
        byte[] symbols,
        string sil
    ) {
        Unit = unit;
        Compiler = compiler;
        Settings = settings;
        Sources = sources;
        Modules = modules;
        Extensions = extensions;
        SymbolBytes = symbols;
        SilText = sil;
    }

    /// <summary>The unit's name: <c>Sable.Core</c>, a package's library.</summary>
    public string Unit { get; }

    /// <summary>The identity of the compiler that wrote it (<see cref="IdentityOf" />).</summary>
    public string Compiler { get; }

    /// <summary>What the unit was compiled for — target and profile — as the build that wrote it said (<see cref="SettingsOf" />).</summary>
    public string Settings { get; }

    /// <summary>The unit's files, by name, with the SHA-256 of each one's text, in the order the compilation had them.</summary>
    public IReadOnlyList<(string File, string Hash)> Sources { get; }

    /// <summary>The modules the unit declares.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>
    ///     What other units' extensions added to the unit's types in the
    ///     compilation it was lowered in: how its methods are named and
    ///     dispatched depends on them (docs/05 §5.4), so a program that adds
    ///     others cannot take its SIL.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>The symbol section's bytes (<see cref="SymbolInterface" />).</summary>
    public byte[] SymbolBytes { get; }

    /// <summary>The unit's SIL, as text.</summary>
    public string SilText { get; }

    /// <summary>
    ///     The identity of a compiler made of these assemblies: the SHA-256 of
    ///     each one's module version id, which a deterministic build derives
    ///     from everything compiled into it — its code and its embedded
    ///     resources, the library's source and the runtime's among them — so
    ///     that two builds of the compiler differ exactly when one of them
    ///     changed, whatever their version numbers say.
    /// </summary>
    public static string IdentityOf(IEnumerable<Assembly> assemblies) {
        var builder = new StringBuilder();
        foreach (var assembly in assemblies.Distinct().OrderBy(static a => a.GetName().Name, StringComparer.Ordinal)) {
            builder.Append(assembly.GetName().Name).Append(':').Append(assembly.ManifestModule.ModuleVersionId.ToString("N")).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>The front end's and lowering's assemblies: what <see cref="IdentityOf" /> is given when nothing else of the compiler is known.</summary>
    public static IEnumerable<Assembly> FrontEnd => [typeof(SyntaxTree).Assembly, typeof(Compilation).Assembly, typeof(SilModule).Assembly];

    /// <summary>The settings a unit is compiled with, as an interface records them: its files' target and profile, and whether reflection is denied.</summary>
    public static string SettingsOf(ParseOptions options, bool denyReflection) =>
        $"{options.TargetOs}-{options.TargetArch}-{CoreLibrary.ProfileOf(options)}{(denyReflection ? "-deny-reflection" : "")}";

    /// <summary>The SHA-256 of a file's text, as <see cref="Sources" /> records it.</summary>
    public static string HashOf(SyntaxTree tree) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tree.Text.Text)));

    /// <summary>
    ///     A library unit's interface: the files <paramref name="owns" />
    ///     picks of the compilation, checked and lowered as a unit of it.
    ///     Null, with the errors, when they do not compile.
    /// </summary>
    public static (LibraryInterface? Interface, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> Errors) Build(
        Compilation compilation,
        string unit,
        Func<SyntaxTree, bool> owns,
        string compiler,
        bool denyReflection = false
    ) {
        var files = compilation.SyntaxTrees.Where(owns).ToList();
        var errors = compilation.GetAllDiagnosticsByFile(owns)
            .Where(d => d.Diagnostic.Severity == DiagnosticSeverity.Error && (d.Tree is null || owns(d.Tree)))
            .ToList();
        if (errors.Count > 0) {
            return (null, errors);
        }

        var (module, lowering) = SilGen.LowerUnit(compilation, new SilUnit(owns), SilProgram.Index(compilation), denyReflection: denyReflection);
        var failed = lowering.Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => (d, (SyntaxTree?)null)).ToList();
        if (failed.Count > 0) {
            return (null, failed);
        }

        var names = files.Select(CoreLibrary.ModuleOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var modules = compilation.Modules.Where(m => names.Contains(m.Name)).OrderBy(static m => m.Name, StringComparer.Ordinal).ToList();
        var settings = SettingsOf(compilation.Options, denyReflection);
        return (new(
            unit,
            compiler,
            settings,
            [..files.Select(static t => (t.Text.FileName, HashOf(t)))],
            [..modules.Select(static m => m.Name)],
            ExtensionsOf(compilation, modules),
            SymbolInterface.Write(modules, compilation.Modules),
            SilPrinter.Print(module, keepBlockOrder: true)
        ), []);
    }

    /// <summary>
    ///     What extensions declared outside the modules add to their types:
    ///     each member, as <c>Module: Type.member</c>, and each conformance,
    ///     sorted — what <see cref="Extensions" /> records.
    /// </summary>
    public static IReadOnlyList<string> ExtensionsOf(Compilation compilation, IEnumerable<ModuleSymbol> modules) {
        var own = modules.ToHashSet(ReferenceEqualityComparer.Instance);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var module in own.Cast<ModuleSymbol>()) {
            Visit(module);
        }

        return [..found];

        void Visit(Symbol container) {
            var members = container switch {
                ModuleSymbol m => m.Members,
                NamedTypeSymbol t => t.Members,
                _ => [],
            };
            foreach (var member in members) {
                if (member.ContainingSymbol is ExtensionSymbol { DeclaringModule: { } declaring } extension && !own.Contains(declaring)) {
                    found.Add($"{declaring.Name}: {extension.ExtendedType.QualifiedName}.{member.Name}");
                }

                if (member is NamedTypeSymbol type) {
                    foreach (var other in type.Extensions) {
                        if (other.DeclaringModule is { } by && !own.Contains(by)) {
                            foreach (var conformance in other.Conformances) {
                                found.Add($"{by.Name}: {type.QualifiedName}: {conformance.ToDisplayString()}");
                            }
                        }
                    }

                    Visit(type);
                }
            }
        }
    }

    /// <summary>
    ///     Why a compilation cannot be compiled against this interface, or
    ///     null when it can: its files of the unit must be the ones the
    ///     interface was built from, compiled with the same settings, and no
    ///     other unit may extend the unit's types otherwise.
    /// </summary>
    public string? Mismatch(Compilation compilation, string settings) {
        if (settings != Settings) {
            return $"the interface of '{Unit}' was built for {Settings}, not {settings}";
        }

        var byName = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees) {
            byName.TryAdd(tree.Text.FileName, tree);
        }

        foreach (var (file, hash) in Sources) {
            if (!byName.TryGetValue(file, out var tree)) {
                return $"the interface of '{Unit}' has '{file}', which the program does not";
            }

            if (HashOf(tree) != hash) {
                return $"'{file}' is not the file the interface of '{Unit}' was built from";
            }
        }

        var modules = compilation.Modules.Where(m => Modules.Contains(m.Name)).ToList();
        var extensions = ExtensionsOf(compilation, modules);
        if (!extensions.SequenceEqual(Extensions, StringComparer.Ordinal)) {
            var added = extensions.Except(Extensions, StringComparer.Ordinal).Concat(Extensions.Except(extensions, StringComparer.Ordinal)).First();
            return $"the program extends '{Unit}' otherwise than the interface was built with ({added})";
        }

        return null;
    }

    /// <summary>Whether a file of a compilation is one of the unit's, by name — what the program does not check or lower again.</summary>
    public bool Covers(SyntaxTree tree) {
        _files ??= Sources.Select(static s => s.File).ToHashSet(StringComparer.Ordinal);
        return _files.Contains(tree.Text.FileName);
    }

    HashSet<string>? _files;

    /// <summary>The unit's symbols, read back, with the modules of the units it was compiled against to resolve their types in.</summary>
    public IReadOnlyList<ModuleSymbol> ReadSymbols(IEnumerable<ModuleSymbol>? dependencies = null) => SymbolInterface.Read(SymbolBytes, dependencies);

    /// <summary>The unit's SIL, parsed afresh: each program it is linked into takes, and changes, its own copy.</summary>
    public SilModule ReadSil() => SilParser.Parse(SilText);

    /// <summary>
    ///     Adds the unit's code to a program's module: every function, type,
    ///     global, C function and export of the unit's SIL the module does not
    ///     define already. What both define — an instance of a generic
    ///     function, a witness thunk, a reflection function — is made the same
    ///     by each, and the program's is kept, as a linker keeps one.
    /// </summary>
    public void LinkInto(SilModule program) {
        var library = ReadSil();
        foreach (var type in library.Types) {
            if (program.FindType(type.Name) is null) {
                program.AddType(type);
            }
        }

        foreach (var function in library.Functions) {
            if (program.Find(function.Name) is null) {
                program.Add(function);
            }
        }

        var globals = program.Globals.Select(static g => g.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var global in library.Globals) {
            if (globals.Add(global.Name)) {
                program.Globals.Add(global);
            }
        }

        foreach (var (name, foreign) in library.Foreign) {
            program.Foreign.TryAdd(name, foreign);
        }

        foreach (var (name, count) in library.ForeignVariadic) {
            program.ForeignVariadic.TryAdd(name, count);
        }

        foreach (var (name, symbol) in library.Exports) {
            program.Exports.TryAdd(name, symbol);
        }
    }

    // =======================================================================
    // The file
    // =======================================================================

    /// <summary>The file's bytes.</summary>
    public byte[] Write() {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true)) {
            writer.Write(Compiler);
            writer.Write(Settings);
            writer.Write(Unit);
            writer.Write(Sources.Count);
            foreach (var (file, hash) in Sources) {
                writer.Write(file);
                writer.Write(hash);
            }

            WriteList(writer, Modules);
            WriteList(writer, Extensions);
            writer.Write(SymbolBytes.Length);
            writer.Write(SymbolBytes);
            var sil = Compress(Encoding.UTF8.GetBytes(SilText));
            writer.Write(sil.Length);
            writer.Write(sil);
        }

        var body = payload.ToArray();
        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(BitConverter.GetBytes(Version));
        output.Write(body);
        output.Write(SHA256.HashData(body));
        return output.ToArray();
    }

    static void WriteList(BinaryWriter writer, IReadOnlyList<string> items) {
        writer.Write(items.Count);
        foreach (var item in items) {
            writer.Write(item);
        }
    }

    /// <summary>
    ///     An interface from its file's bytes; null when they are not one this
    ///     compiler can use — another format or version, another compiler's,
    ///     cut short or corrupt — which a build answers by compiling the unit
    ///     again. Its symbols and SIL are read when they are asked for.
    /// </summary>
    /// <param name="compiler">The reading compiler's identity: an interface another compiler wrote is not read.</param>
    public static LibraryInterface? Read(ReadOnlySpan<byte> bytes, string compiler) {
        const int header = 4 + sizeof(int), hash = 32;
        if (bytes.Length < header + hash || !bytes[..4].SequenceEqual(Magic) || BitConverter.ToInt32(bytes[4..header]) != Version) {
            return null;
        }

        var body = bytes[header..^hash];
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes[^hash..])) {
            return null;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(body.ToArray(), writable: false), Encoding.UTF8);
            if (reader.ReadString() != compiler) {
                return null;
            }

            var settings = reader.ReadString();
            var unit = reader.ReadString();
            var count = Count(reader, body.Length);
            var sources = new List<(string, string)>(count);
            for (var i = 0; i < count; i++) {
                sources.Add((reader.ReadString(), reader.ReadString()));
            }

            var modules = ReadList(reader, body.Length);
            var extensions = ReadList(reader, body.Length);
            var symbols = reader.ReadBytes(Count(reader, body.Length));
            var sil = reader.ReadBytes(Count(reader, body.Length));
            if (reader.BaseStream.Position != reader.BaseStream.Length) {
                return null;
            }

            return new(unit, compiler, settings, sources, modules, extensions, symbols, Encoding.UTF8.GetString(Decompress(sil)));
        } catch (Exception failure) when (failure is EndOfStreamException or IOException or InvalidDataException or ArgumentException
                                              or DecoderFallbackException or OverflowException) {
            return null;
        }
    }

    static int Count(BinaryReader reader, int limit) {
        var count = reader.ReadInt32();
        return count >= 0 && count <= limit ? count : throw new InvalidDataException("a length past the end of the file");
    }

    static List<string> ReadList(BinaryReader reader, int limit) {
        var count = Count(reader, limit);
        var items = new List<string>(count);
        for (var i = 0; i < count; i++) {
            items.Add(reader.ReadString());
        }

        return items;
    }

    static byte[] Compress(byte[] data) {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)) {
            brotli.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Brotli's output, refused past 1 GB: a corrupt length must not take the machine's memory.</summary>
    static byte[] Decompress(byte[] data) {
        using var input = new BrotliStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0) {
            output.Write(buffer, 0, read);
            if (output.Length > 1L << 30) {
                throw new InvalidDataException("the SIL section is larger than any unit's");
            }
        }

        return output.ToArray();
    }
}
