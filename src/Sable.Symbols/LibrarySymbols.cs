using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The declarations of a library's files as an interface has them
///     (docs/05 §5.4, #182): its modules' bound symbols
///     (<see cref="SymbolInterface" />), and what a compilation needs besides to
///     take them in place of declaring the files — where each is declared, the
///     accessors of its properties and subscripts, its array extensions and
///     derived members. A compilation created with them
///     (<see cref="Compilation.Create(IEnumerable{SyntaxTree}, ParseOptions, LibrarySymbols)" />)
///     parses, declares and binds none of the library's files until something
///     asks for what only a file says: a body it lowers, a default argument, a
///     place to point at.
/// </summary>
/// <remarks>
///     <para>
///         What they say of the library is what declaring its source says, for a
///         program that does not change it: one bringing a file of the library, or
///         extending one of its types, is compiled from the source.
///     </para>
///     <para>
///         A library that has what they do not hold — a generator, a lazy or
///         observed property, an inline layout, a <c>comptime</c> block, a
///         result or a property hidden behind <c>some</c>, an attribute of its own
///         — has none (<see cref="Of" /> gives null), and is always declared from
///         source. The standard library has none of those.
///     </para>
/// </remarks>
public sealed class LibrarySymbols {
    /// <summary>The format's version: bump when what is written, or what it means, changes.</summary>
    const int Version = 1;

    readonly byte[] _symbols;
    readonly byte[] _facts;

    LibrarySymbols(IReadOnlyList<SyntaxTree> trees, byte[] symbols, byte[] facts) {
        Trees = trees;
        _symbols = symbols;
        _facts = facts;
        Modules = [..trees.Select(static t => t.Header.Module!).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The library's files, in the order a compilation has them: last, the core's at the end.</summary>
    public IReadOnlyList<SyntaxTree> Trees { get; }

    /// <summary>The modules the files declare.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>
    ///     How compilations that are given no library symbols get the library's
    ///     declarations: from its source — the default — or, with
    ///     <c>SABLE_LIBRARY_SYMBOLS=interface</c>, from symbols made of it in
    ///     this process, which is what the tests run to hold the two to the same
    ///     answers. <c>SABLE_LIBRARY_SYMBOLS=source</c> has none used at all.
    /// </summary>
    public static LibrarySymbolsMode Mode { get; } = Environment.GetEnvironmentVariable("SABLE_LIBRARY_SYMBOLS") switch {
        "interface" => LibrarySymbolsMode.Interface,
        "source" => LibrarySymbolsMode.Source,
        _ => LibrarySymbolsMode.Given,
    };

    // =======================================================================
    // Making
    // =======================================================================

    /// <summary>
    ///     The symbols of the library <paramref name="library" /> — the core's
    ///     files come with it — declared from its source; null when it does not
    ///     declare cleanly, or has what they do not hold.
    /// </summary>
    public static LibrarySymbols? Of(IEnumerable<SyntaxTree> library, ParseOptions options) => Of(library, options, out _);

    /// <param name="why">Why it has none: what it has that they do not hold.</param>
    public static LibrarySymbols? Of(IEnumerable<SyntaxTree> library, ParseOptions options, out string? why) {
        var compilation = Compilation.FromSource([..library], options);
        why = compilation.GetDiagnostics().FirstOrDefault(static d => d.Severity == DiagnosticSeverity.Error) is { } error ? $"an error: {error.Message}" : NotHeld(compilation);
        if (why is not null) {
            return null;
        }

        var modules = compilation.Modules.ToList();
        var symbols = SymbolInterface.Write(modules, modules, compilation.ExtensionRoots(), out var table);
        try {
            return new(compilation.SyntaxTrees, symbols, WriteFacts(compilation, table));
        } catch (NotHeldException not) {
            why = not.Message;
            return null;
        }
    }

    /// <summary>The library has something its symbols do not hold.</summary>
    sealed class NotHeldException(string what) : Exception(what);

    static string? NotHeld(Compilation compilation) =>
        compilation.LazyProperties.Keys.Concat(compilation.ObservedProperties.Keys).FirstOrDefault() is { } property ? $"the lazy or observed property '{property}'"
        : compilation.HasUserAttributes ? "an attribute of its own"
        : compilation.HasInlineLayouts ? "an inline layout"
        : compilation.SyntaxTrees.FirstOrDefault(static t => t.Header.Module is null) is { } tree ? $"'{tree.Text.FileName}', which declares no module"
        : compilation.SyntaxTrees.FirstOrDefault(static t => t.Text.Text.Contains("comptime", StringComparison.Ordinal)
            && t.Root.DescendantTokens().Any(static token => token.Kind == SyntaxKind.ComptimeKeyword)) is { } comptime ? $"a 'comptime' block, in '{comptime.Text.FileName}'"
        : null;

    /// <summary>What an accessor is: code of its own, or a protocol's requirement.</summary>
    enum AccessorKind : byte {
        Declared = 1,
        Requirement = 2,
    }

    static byte[] WriteFacts(Compilation compilation, IReadOnlyList<Symbol> table) {
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream, Encoding.UTF8);
        var trees = compilation.SyntaxTrees.Select(static (tree, i) => (tree, i)).ToDictionary(static p => p.tree, static p => p.i);
        var ids = new Dictionary<Symbol, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < table.Count; i++) {
            ids[table[i]] = i;
        }

        output.Write7BitEncodedInt(table.Count);
        foreach (var symbol in table) {
            if (symbol.DeclaringSyntax is [var declaration, ..]) {
                var tree = compilation.TreeOf(declaration)!;
                output.Write7BitEncodedInt(trees[tree] + 1);
                WritePlace(output, declaration);
                output.Write(ReferenceEquals(compilation.GetDeclaredSymbol(declaration), symbol));
            } else {
                output.Write7BitEncodedInt(0);
            }

            switch (symbol) {
                case FunctionSymbol function:
                    if (function.IsGenerator || function.HiddenResultType is not null || function.ReturnType is OpaqueTypeSymbol { Origin: not null }) {
                        throw new NotHeldException($"the generator, or function with a hidden result, '{function}'");
                    }

                    if (function.FunctionKind == FunctionKind.Subscript) {
                        output.Write(function.HasSetter);
                        output.Write(function.IsExpressionBodied);
                        WriteAccessors(output, compilation, function);
                    }

                    break;
                case FieldSymbol field:
                    if (field.HiddenType is not null || field.Type is OpaqueTypeSymbol { Origin: not null }) {
                        throw new NotHeldException($"the property of a hidden type '{field}'");
                    }

                    if (field.IsConstant) {
                        output.Write(field.DeclaringSyntax is [ConstDeclarationSyntax constant]
                            && compilation.TreeOf(constant) is { } file
                            && compilation.GetSemanticModel(file).GetMemberModel(constant).GetFieldInitializer(field) is BoundLiteral);
                    } else {
                        WriteAccessors(output, compilation, field);
                    }

                    break;
                case AliasSymbol { Type: RecursiveUnionTypeSymbol }:
                    throw new NotHeldException($"the recursive union '{symbol}'");
            }
        }

        output.Write7BitEncodedInt(compilation.ArrayCarriers.Count);
        foreach (var carrier in compilation.ArrayCarriers) {
            output.Write7BitEncodedInt(ids[carrier]);
        }

        output.Write7BitEncodedInt(compilation.Derivations.Count);
        foreach (var derivation in compilation.Derivations) {
            output.Write7BitEncodedInt(ids[derivation.Function]);
            output.Write7BitEncodedInt(ids[derivation.Type]);
            output.Write7BitEncodedInt(ids[derivation.Protocol]);
            output.Write((byte)derivation.Member);
            output.Write7BitEncodedInt(trees[derivation.Tree]);
        }

        output.Write7BitEncodedInt(compilation.InheritedInitializers.Count);
        foreach (var (init, inherited) in compilation.InheritedInitializers) {
            output.Write7BitEncodedInt(ids[init]);
            output.Write7BitEncodedInt(ids[inherited.Inherited]);
            output.Write7BitEncodedInt(trees[inherited.Tree]);
        }

        var callees = compilation.DeclaredAutoclosureCallees.Order(StringComparer.Ordinal).ToList();
        output.Write7BitEncodedInt(callees.Count);
        foreach (var name in callees) {
            output.Write(name);
        }

        output.Flush();
        return stream.ToArray();
    }

    static void WritePlace(BinaryWriter output, SyntaxNode node) {
        output.Write7BitEncodedInt(node.Span.Start);
        output.Write7BitEncodedInt(node.Span.Length);
        output.Write7BitEncodedInt((int)node.Kind);
    }

    /// <summary>A property's or a subscript's accessors, in the order written.</summary>
    static void WriteAccessors(BinaryWriter output, Compilation compilation, Symbol owner) {
        // A backing field is declared where its property is: the accessors are the property's.
        var accessors = owner.DeclaringSyntax is [var declaration, ..] && ReferenceEquals(compilation.GetDeclaredSymbol(declaration), owner)
            ? declaration switch {
                FieldDeclarationSyntax { Accessors: { } list } => list.Accessors.ToList(),
                SubscriptDeclarationSyntax { ExpressionBody: null, Accessors: { } list } => list.Accessors.ToList(),
                _ => [],
            }
            : [];
        var model = accessors.Count > 0 ? compilation.GetSemanticModel(compilation.TreeOf(accessors[0])!) : null;
        var found = accessors
            .Select(a => (Syntax: a, Declared: model!.GetAccessorDeclaration(a), Required: model.GetRequirementAccessor(a)))
            .Where(static a => a.Declared is not null || a.Required is not null)
            .ToList();
        output.Write7BitEncodedInt(found.Count);
        foreach (var (syntax, declared, required) in found) {
            var function = declared ?? required!;
            if (function.IsGenerator) {
                throw new NotHeldException($"the generator '{owner}'");
            }

            output.Write((byte)(declared is not null ? AccessorKind.Declared : AccessorKind.Requirement));
            WritePlace(output, syntax);
            output.Write(function.Name);
            output.Write(function.IsAsync);
            output.Write(function.HasBody);
            output.Write(function.IsNonisolated);
            output.Write(function.IsMutating ? function.Parameters[^1].Name : "");
            output.Write7BitEncodedInt(function.Attributes.Length);
            foreach (var attribute in function.Attributes) {
                output.Write(attribute.Name);
                output.Write(attribute.ArgumentList is not null);
                output.Write(attribute.ArgumentList ?? "");
                output.Write(attribute.FromExtension);
            }
        }
    }

    // =======================================================================
    // The file
    // =======================================================================

    /// <summary>The symbols as bytes, which <see cref="Read" /> reads back for the same files.</summary>
    public byte[] Write() {
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream, Encoding.UTF8);
        output.Write(Version);
        output.Write(SymbolInterface.Version);
        output.Write(Trees.Count);
        foreach (var tree in Trees) {
            output.Write(tree.Text.FileName);
        }

        output.Write(_symbols.Length);
        output.Write(_symbols);
        output.Write(_facts.Length);
        output.Write(_facts);
        output.Flush();
        stream.Write(SHA256.HashData(stream.ToArray()));
        return stream.ToArray();
    }

    /// <summary>
    ///     What <see cref="Write" /> wrote of these files, each the same text it
    ///     was written of — which is for what keeps the bytes to know, by the
    ///     compiler whose build carries the files. Null for another version,
    ///     other files, or bytes cut short or changed.
    /// </summary>
    public static LibrarySymbols? Read(byte[] bytes, IReadOnlyList<SyntaxTree> trees) {
        var length = bytes.Length - SHA256.HashSizeInBytes;
        if (length < 0 || !SHA256.HashData(bytes.AsSpan(0, length)).AsSpan().SequenceEqual(bytes.AsSpan(length))) {
            return null;
        }

        try {
            using var input = new BinaryReader(new MemoryStream(bytes, 0, length, writable: false), Encoding.UTF8);
            if (input.ReadInt32() != Version || input.ReadInt32() != SymbolInterface.Version || input.ReadInt32() != trees.Count) {
                return null;
            }

            foreach (var tree in trees) {
                if (input.ReadString() != tree.Text.FileName) {
                    return null;
                }
            }

            var symbols = input.ReadBytes(input.ReadInt32());
            var facts = input.ReadBytes(input.ReadInt32());
            return input.BaseStream.Position == length ? new(trees, symbols, facts) : null;
        } catch (Exception failure) when (failure is IOException or ArgumentException) {
            return null;
        }
    }

    // =======================================================================
    // A compilation's
    // =======================================================================

    /// <summary>Whether a compilation's library files are these, and the rest of its files declare nothing in their modules.</summary>
    internal bool Covers(Compilation compilation) {
        var own = compilation.SyntaxTrees.Length - Trees.Count;
        if (own < 0) {
            return false;
        }

        for (var i = 0; i < Trees.Count; i++) {
            if (!ReferenceEquals(compilation.SyntaxTrees[own + i], Trees[i])) {
                return false;
            }
        }

        return !compilation.SyntaxTrees.Take(own).Any(t => t.Header.Module is { } module && Modules.Contains(module));
    }

    static readonly ConcurrentDictionary<string, Lazy<LibrarySymbols?>> Made = new(StringComparer.Ordinal);

    /// <summary>With <see cref="LibrarySymbolsMode.Interface" />, the symbols of the library files among <paramref name="trees" />, made once per process.</summary>
    internal static LibrarySymbols? Forced(IReadOnlyList<SyntaxTree> trees, ParseOptions options) {
        if (Mode != LibrarySymbolsMode.Interface) {
            return null;
        }

        var target = trees.FirstOrDefault(static t => !CoreLibrary.Contains(t))?.Options ?? options;
        List<SyntaxTree> library = [..trees.Where(static t => t.Options.Workspace == "sable" && t.Text.FileName.StartsWith("lib/Sable.", StringComparison.Ordinal) && !CoreLibrary.Contains(t))];
        if (trees.Except(library).Any(t => CoreLibrary.IsStandardLibrary(t.Header.Module) && !CoreLibrary.Contains(t))) {
            return null;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{target.TargetOs} {target.TargetArch} {CoreLibrary.ProfileOf(target)} ")
            + string.Join(" ", library.Select(static t => $"{t.Text.FileName}:{t.Text.Text.GetHashCode(StringComparison.Ordinal)}"));
        return Made.GetOrAdd(key, _ => new(() => Of(library, target))).Value is { } made && made.Trees.Zip(CoreOf(library, target)).All(static p => ReferenceEquals(p.First, p.Second)) ? made : null;

        static IEnumerable<SyntaxTree> CoreOf(List<SyntaxTree> library, ParseOptions target) => library.Concat(CoreLibrary.TreesFor(target));
    }

    /// <summary>The modules read, fresh symbols each time, with every declaration in the order it was written.</summary>
    internal IReadOnlyList<ModuleSymbol> ReadModules(out IReadOnlyList<Symbol> table) => SymbolInterface.Read(_symbols, null, out table);

    /// <summary>Reads the facts into <paramref name="compilation" />'s symbols: where each is declared, accessors, and the rest.</summary>
    internal void ReadFacts(Compilation compilation, IReadOnlyList<Symbol> table, InterfaceLibrary into) {
        using var input = new BinaryReader(new MemoryStream(_facts, writable: false), Encoding.UTF8);
        if (input.Read7BitEncodedInt() != table.Count) {
            throw new InvalidDataException("the facts are of other symbols");
        }

        foreach (var symbol in table) {
            var file = input.Read7BitEncodedInt();
            if (file > 0) {
                symbol.Source = ReadPlace(Trees[file - 1]);
                if (input.ReadBoolean()) {
                    into.Add(Trees[file - 1], symbol);
                }
            }

            switch (symbol) {
                case FunctionSymbol function:
                    function.IsGenerator = false;
                    if (function.FunctionKind == FunctionKind.Subscript) {
                        function.HasSetter = input.ReadBoolean();
                        function.IsExpressionBodied = input.ReadBoolean();
                        function.InterfaceAccessors = ReadAccessors(function, file);
                    }

                    break;
                case FieldSymbol { IsConstant: true } constant:
                    constant.IsKnownLiteral = input.ReadBoolean();
                    constant.ValueResolver = () => compilation.TypeBinder.ValueOf(constant);
                    break;
                case FieldSymbol field:
                    field.InterfaceAccessors = ReadAccessors(field, file);
                    break;
            }
        }

        var carriers = input.Read7BitEncodedInt();
        for (var i = 0; i < carriers; i++) {
            into.Carriers.Add((NamedTypeSymbol)table[input.Read7BitEncodedInt()]);
        }

        var derivations = input.Read7BitEncodedInt();
        for (var i = 0; i < derivations; i++) {
            into.Derivations.Add(new(
                (FunctionSymbol)table[input.Read7BitEncodedInt()],
                (NamedTypeSymbol)table[input.Read7BitEncodedInt()],
                (NamedTypeSymbol)table[input.Read7BitEncodedInt()],
                (DerivedMember)input.ReadByte(),
                Trees[input.Read7BitEncodedInt()]
            ));
        }

        var inheritedInitializers = input.Read7BitEncodedInt();
        for (var i = 0; i < inheritedInitializers; i++) {
            var init = (FunctionSymbol)table[input.Read7BitEncodedInt()];
            var type = (NamedTypeSymbol)init.ContainingSymbol!;
            into.InheritedInitializers[init] = new(
                (FunctionSymbol)table[input.Read7BitEncodedInt()],
                type.BaseTypes.OfType<NamedTypeSymbol>().First(static b => b.TypeKind == TypeKind.Class),
                type,
                Trees[input.Read7BitEncodedInt()]
            );
        }

        var callees = input.Read7BitEncodedInt();
        for (var i = 0; i < callees; i++) {
            into.AutoclosureCallees.Add(input.ReadString());
        }

        SymbolSource ReadPlace(SyntaxTree tree) => new(tree, input.Read7BitEncodedInt(), input.Read7BitEncodedInt(), (SyntaxKind)input.Read7BitEncodedInt());

        System.Collections.Immutable.ImmutableArray<FunctionSymbol> ReadAccessors(Symbol owner, int file) {
            var count = input.Read7BitEncodedInt();
            if (count == 0) {
                return [];
            }

            var accessors = System.Collections.Immutable.ImmutableArray.CreateBuilder<FunctionSymbol>(count);
            for (var i = 0; i < count; i++) {
                var kind = (AccessorKind)input.ReadByte();
                var source = ReadPlace(Trees[file - 1]);
                var name = input.ReadString();
                var isAsync = input.ReadBoolean();
                var hasBody = input.ReadBoolean();
                var nonisolated = input.ReadBoolean();
                var value = input.ReadString();
                var attributes = System.Collections.Immutable.ImmutableArray.CreateBuilder<SymbolAttribute>();
                for (var attribute = input.Read7BitEncodedInt(); attribute > 0; attribute--) {
                    var attributeName = input.ReadString();
                    var hasList = input.ReadBoolean();
                    var list = input.ReadString();
                    attributes.Add(new(attributeName, hasList ? list : null, input.ReadBoolean()));
                }

                var accessor = SemanticModel.Accessor(compilation, name, owner, null, isAsync, value.Length == 0 ? null : value,
                    owner is FieldSymbol field ? () => field.Type : () => ((FunctionSymbol)owner).ReturnType);
                accessor.Source = source;
                accessor.IsSynthesized = false;
                accessor.IsGenerator = false;
                accessor.HasBody = hasBody;
                accessor.IsNonisolated = nonisolated;
                accessor.Attributes = attributes.ToImmutable();
                if (value.Length > 0) {
                    accessor.Parameters[^1].Source = source;
                }

                if (kind == AccessorKind.Requirement) {
                    into.Requirements.Add(accessor);
                }

                accessors.Add(accessor);
            }

            return accessors.MoveToImmutable();
        }
    }
}

/// <summary>Where a compilation given no <see cref="LibrarySymbols" /> gets the library's declarations.</summary>
public enum LibrarySymbolsMode {
    /// <summary>From the symbols it is given, or else its source.</summary>
    Given,

    /// <summary>From symbols, made in the process where none are given.</summary>
    Interface,

    /// <summary>From its source: no symbols are given or used.</summary>
    Source,
}
