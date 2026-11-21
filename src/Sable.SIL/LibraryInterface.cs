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
    public const int Version = 2;

    static ReadOnlySpan<byte> Magic => "SBIL"u8;

    LibraryInterface(
        string unit,
        string compiler,
        string settings,
        IReadOnlyList<(string File, string Hash)> sources,
        IReadOnlyList<string> modules,
        IReadOnlyList<string> extensions,
        byte[] symbols,
        string sil,
        IReadOnlyDictionary<string, bool> generics,
        IReadOnlySet<string> inlinable,
        IReadOnlySet<string>? debugDescribing = null
    ) {
        Unit = unit;
        Compiler = compiler;
        Settings = settings;
        Sources = sources;
        Modules = modules;
        Extensions = extensions;
        SymbolBytes = symbols;
        SilText = sil;
        Generics = generics;
        Inlinable = inlinable;
        DebugDescribing = debugDescribing ?? new SortedSet<string>(StringComparer.Ordinal);
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
    ///     The generic functions of the unit compiled once for every type into
    ///     its archive (#297 stage 7), by name without <c>$generic</c>, and
    ///     whether each writes out a value of a type it is given: a unit
    ///     compiled against the library calls them there rather than lowering
    ///     them again (<see cref="CompiledGenerics" />). Empty for a unit
    ///     compiled specialised.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Generics { get; }

    /// <summary>Of <see cref="Generics" />, those other units may copy to specialise, whose code <see cref="SilText" /> holds.</summary>
    public IReadOnlySet<string> Inlinable { get; }

    /// <summary>Of <see cref="Generics" />, those that write a value out with <c>?</c> (<see cref="SilUnitInfo.DebugDescribing" />).</summary>
    public IReadOnlySet<string> DebugDescribing { get; }

    /// <summary>What a unit lowered against this library is told of the generic code its archive holds.</summary>
    public CompiledGenerics CompiledGenerics() => new(Covers, Generics, Inlinable, DebugDescribing);

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
        $"{options.TargetOs}-{options.TargetArch}-{CoreLibrary.ProfileOf(options)}{(denyReflection ? "-deny-reflection" : "")}{(SilGen.Generics == GenericsMode.Unspecialised ? "-unspecialised" : "")}";

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

        var (module, lowering) = SilGen.LowerUnit(compilation, new SilUnit(owns) { CompilesGenerics = true }, SilProgram.Index(compilation), denyReflection: denyReflection);
        var failed = lowering.Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => (d, (SyntaxTree?)null)).ToList();
        if (failed.Count > 0) {
            return (null, failed);
        }

        return (Of(compilation, unit, owns, compiler, SettingsOf(compilation.Options, denyReflection), SilPrinter.Print(module, keepBlockOrder: true), module.Unit), []);
    }

    /// <summary>
    ///     The interface of a unit a build has lowered already: its files'
    ///     symbols, and <paramref name="sil" />, the unit's SIL as lowering
    ///     made it — before the passes — printed with each function's block
    ///     order kept (<see cref="SilPrinter.Print(SilModule, bool)" />).
    /// </summary>
    /// <param name="lowered">What lowering the unit recorded of its generic code compiled once (<see cref="Generics" />); null for none.</param>
    public static LibraryInterface Of(Compilation compilation, string unit, Func<SyntaxTree, bool> owns, string compiler, string settings, string sil, SilUnitInfo? lowered = null) {
        var files = compilation.SyntaxTrees.Where(owns).ToList();
        var names = files.Select(CoreLibrary.ModuleOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var modules = compilation.Modules.Where(m => names.Contains(m.Name)).OrderBy(static m => m.Name, StringComparer.Ordinal).ToList();
        return new(
            unit,
            compiler,
            settings,
            [..files.Select(static t => (t.Text.FileName, HashOf(t)))],
            [..modules.Select(static m => m.Name)],
            ExtensionsOf(compilation, modules),
            SymbolInterface.Write(modules, compilation.Modules),
            sil,
            lowered?.Generics ?? new SortedDictionary<string, bool>(StringComparer.Ordinal),
            lowered?.Inlinable ?? new SortedSet<string>(StringComparer.Ordinal),
            lowered?.DebugDescribing
        );
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

        // What the library's generic code compiled once finds at run time:
        // the describers and error erasers it made.
        foreach (var (name, describer) in library.Describers) {
            program.Describers.TryAdd(name, describer);
        }

        foreach (var (name, describer) in library.DebugDescribers) {
            program.DebugDescribers.TryAdd(name, describer);
        }

        foreach (var (name, eraser) in library.ErrorErasers) {
            program.ErrorErasers.TryAdd(name, eraser);
        }

        foreach (var (name, boxer) in library.Boxers) {
            program.Boxers.TryAdd(name, boxer);
        }
    }

    // =======================================================================
    // Inlinable code (#297 stage 7)
    // =======================================================================

    /// <summary>
    ///     The SIL of a library unit that other units may copy, as its
    ///     interface keeps it: its generic functions compiled once that are
    ///     inlinable (<see cref="SilUnitInfo.Inlinable" />), and what they
    ///     refer to that the unit's archive does not define for every unit
    ///     linked with it — closures, thunks, instances of generic functions —
    ///     each with what it refers to in turn; and, of each class the code
    ///     names, its vtable's and its deinit's code, which a unit that has
    ///     not made the class needs to make it. The rest of the unit's code a
    ///     unit compiled against it calls in the archive. Printed before the
    ///     passes, with each function's own block order.
    /// </summary>
    public static string InlinableSil(SilModule module, SilUnitInfo info) {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(info.Inlinable.Select(static name => name + "$generic"));
        while (pending.TryPop(out var name)) {
            if (module.Find(name) is not { } function || !kept.Add(name)) {
                continue;
            }

            foreach (var reference in References(function)) {
                if (!info.Owned.Contains(reference.Function)) {
                    pending.Push(reference.Function);
                }
            }

            foreach (var type in NominalsOf(function).OfType<ClassType>()) {
                if (classes.Add(type.Name)) {
                    foreach (var method in ClassCode(type)) {
                        pending.Push(method);
                    }
                }
            }
        }

        return kept.Count == 0 ? "" : SilPrinter.Print(module, keepBlockOrder: true, f => kept.Contains(f.Name));
    }

    static IEnumerable<FunctionRefInst> References(SilFunction function) =>
        function.Blocks.SelectMany(static b => b.Instructions).OfType<FunctionRefInst>();

    /// <summary>The functions a class's metadata names: its vtable's, and its deinit.</summary>
    static IEnumerable<string> ClassCode(ClassType type) =>
        type.VTable.Select(static m => m.Function).Concat(type.Deinit is { } deinit ? [deinit] : []);

    /// <summary>The named types a function's code mentions — in its signature, its blocks' arguments and its instructions' types — and those they hold.</summary>
    static List<NominalType> NominalsOf(SilFunction function) {
        var seen = new HashSet<SilType>(ReferenceEqualityComparer.Instance);
        var found = new List<NominalType>();
        Visit(function.Type);
        foreach (var block in function.Blocks) {
            foreach (var argument in block.Arguments) {
                Visit(argument.Type);
            }

            foreach (var instruction in block.Instructions) {
                Visit(instruction.Type);
            }
        }

        return found;

        void Visit(SilType? type) {
            if (type is null || !seen.Add(type)) {
                return;
            }

            if (type is NominalType nominal) {
                found.Add(nominal);
            }

            switch (type) {
                case ClassType classType:
                    Visit(classType.Superclass);
                    foreach (var field in classType.Fields) {
                        Visit(field.Type);
                    }

                    break;
                case StructType structType:
                    foreach (var field in structType.Fields) {
                        Visit(field.Type);
                    }

                    break;
                case EnumType enumType:
                    foreach (var @case in enumType.Cases) {
                        Visit(@case.Payload);
                    }

                    break;
                case TupleType tuple:
                    foreach (var element in tuple.Elements) {
                        Visit(element);
                    }

                    break;
                case FunctionType signature:
                    foreach (var parameter in signature.Parameters) {
                        Visit(parameter);
                    }

                    Visit(signature.Result);
                    Visit(signature.Thrown);
                    break;
                case ThickFunctionType thick:
                    Visit(thick.Signature);
                    break;
                case ArrayType array:
                    Visit(array.Element);
                    break;
                case SliceType slice:
                    Visit(slice.Element);
                    break;
                case InlineArrayType inline:
                    Visit(inline.Element);
                    break;
                case AddressType address:
                    Visit(address.Pointee);
                    break;
                case InlinePointerType pointer:
                    Visit(pointer.Pointee);
                    break;
                case WeakStorageType weak:
                    Visit(weak.Optional);
                    break;
                case UnownedStorageType unowned:
                    Visit(unowned.Referent);
                    break;
            }
        }
    }

    /// <summary>
    ///     Copies into a unit's module, lowered against the library, the
    ///     library's inlinable generic code it calls — and what that refers to
    ///     of <see cref="InlinableSil" /> — so that the specialisation pass
    ///     makes copies of it for the types the unit knows (#297 stage 7).
    ///     What the copied code refers to that the interface does not hold,
    ///     the library's archive defines: the unit declares it. A type the
    ///     copied code names that the unit has not made it takes from the
    ///     interface, with its class's code. A copy the pass does not use is
    ///     the unit's own, linked once with the library's.
    /// </summary>
    /// <returns>How many functions were copied.</returns>
    public int LinkInlinable(SilModule unit) {
        if (Inlinable.Count == 0 || unit.Unit is not { } info) {
            return 0;
        }

        var roots = info.External.Keys.Where(name => name.EndsWith("$generic", StringComparison.Ordinal) && Inlinable.Contains(name[..^"$generic".Length])).ToList();
        if (roots.Count == 0) {
            return 0;
        }

        // Parsed afresh: each unit takes, and changes, a copy of its own.
        var library = SilParser.Parse(SilText);
        var copied = 0;
        var globals = unit.Globals.Select(static g => g.Name).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out var name)) {
            if (unit.Find(name) is not null || library.Find(name) is not { } function) {
                continue;
            }

            unit.Add(function);
            info.External.Remove(name);
            copied++;
            foreach (var reference in References(function)) {
                Need(reference.Function, (FunctionType)reference.Type);
            }

            foreach (var global in function.Blocks.SelectMany(static b => b.Instructions).OfType<GlobalAddrInst>()) {
                if (globals.Add(global.Global) && library.Globals.FirstOrDefault(g => g.Name == global.Global) is { Name: not null } declared) {
                    unit.Globals.Add(declared);
                }
            }

            // A type the unit has not made, the interface's; a class's with its code.
            foreach (var type in NominalsOf(function)) {
                if (unit.FindType(type.Name) is not null) {
                    continue;
                }

                unit.AddType(type);
                if (type is ClassType classType) {
                    foreach (var method in ClassCode(classType)) {
                        Need(method, null);
                    }
                }
            }
        }

        return copied;

        // What copied code names: copied in turn from the interface, else
        // declared, to be called in the archive.
        void Need(string name, FunctionType? type) {
            if (unit.Find(name) is not null) {
                return;
            }

            if (library.Find(name) is not null) {
                pending.Push(name);
            } else if (library.Foreign.TryGetValue(name, out var foreign)) {
                unit.Foreign.TryAdd(name, foreign);
                if (library.ForeignVariadic.TryGetValue(name, out var count)) {
                    unit.ForeignVariadic.TryAdd(name, count);
                }
            } else if (type is not null && !unit.Foreign.ContainsKey(name)) {
                info.External.TryAdd(name, type);
            }
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
            writer.Write(Generics.Count);
            foreach (var (name, describes) in Generics.OrderBy(static g => g.Key, StringComparer.Ordinal)) {
                writer.Write(name);
                writer.Write((byte)((describes ? 1 : 0) | (Inlinable.Contains(name) ? 2 : 0) | (DebugDescribing.Contains(name) ? 4 : 0)));
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
            var generics = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            var inlinable = new SortedSet<string>(StringComparer.Ordinal);
            var debugDescribing = new SortedSet<string>(StringComparer.Ordinal);
            for (var left = Count(reader, body.Length); left > 0; left--) {
                var name = reader.ReadString();
                var flags = reader.ReadByte();
                if (flags > 7 || !generics.TryAdd(name, (flags & 1) != 0)) {
                    return null;
                }

                if ((flags & 2) != 0) {
                    inlinable.Add(name);
                }

                if ((flags & 4) != 0) {
                    debugDescribing.Add(name);
                }
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length) {
                return null;
            }

            return new(unit, compiler, settings, sources, modules, extensions, symbols, Encoding.UTF8.GetString(Decompress(sil)), generics, inlinable, debugDescribing);
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
