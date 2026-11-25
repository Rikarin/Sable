using System.Collections.Concurrent;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     One unit of a program compiled separately (docs/05 §5.4): the files it
///     owns, whose functions it lowers; every other function of the program it
///     only declares. A build compiles each unit to an object file and links
///     them, so a change to one unit's function bodies rebuilds only that unit.
/// </summary>
/// <remarks>
///     <para>
///         The compilation still holds the whole program — every declaration,
///         so names, signatures, layouts and vtables come out the same in each
///         unit — but only the owned files' bodies are bound and lowered.
///     </para>
///     <para>
///         What several units may need is made in each that needs it, and the
///         linker keeps one: a generic function's instances (lowered where they
///         are used, like C++ templates), witness thunks, reflection functions,
///         class metadata (whose address is a class's identity) and the
///         globals' storage. The unit holding the entry point also initialises
///         and destroys every global.
///     </para>
/// </remarks>
/// <param name="Owns">Whether a file is one of this unit's.</param>
public sealed record SilUnit(Func<SyntaxTree, bool> Owns) {
    /// <summary>
    ///     The library units compiled before this one whose generic code is
    ///     compiled once in their archives (#297 stage 7): a generic function
    ///     of theirs this unit calls with types it knows is called there, not
    ///     lowered here again — its body is neither bound nor lowered.
    /// </summary>
    public IReadOnlyList<CompiledGenerics> Libraries { get; init; } = [];

    /// <summary>
    ///     The unit is a library's, kept as an archive other programs link
    ///     (<see cref="CompiledGenerics" />): in an unspecialised build it
    ///     compiles every generic function of its files that can be compiled
    ///     once, whether or not its own code calls it, and defines each for
    ///     every unit linked with it.
    /// </summary>
    public bool CompilesGenerics { get; init; }
}

/// <summary>
///     What a library unit compiled of its generic functions (#297 stage 7,
///     #182): each generic function it compiled once for every type, by its
///     name, and whether that code writes out a value of a type it is given
///     — which tells a caller to register that type's describer. Its interface
///     (<see cref="LibraryInterface" />) records it, so that a unit lowered
///     against the library calls those functions in its archive without
///     binding their bodies, and lowers again only the generic functions the
///     library left to be specialised.
/// </summary>
/// <param name="Covers">Whether a file is one of the library's.</param>
/// <param name="Functions">Each generic function compiled once, by its name without <c>$generic</c>, and whether it describes values.</param>
/// <param name="Inlinable">
///     Those whose bodies other units may copy for the types they know: the
///     interface's SIL has them (<see cref="SilUnitInfo.Inlinable" />).
/// </param>
/// <param name="DebugDescribing">Of <paramref name="Functions" />, those that write a value of a type they are given out with <c>?</c> (<see cref="SilUnitInfo.DebugDescribing" />).</param>
public sealed record CompiledGenerics(Func<SyntaxTree, bool> Covers, IReadOnlyDictionary<string, bool> Functions, IReadOnlySet<string> Inlinable, IReadOnlySet<string>? DebugDescribing = null);

/// <summary>What separate lowering tells the code generator about a unit's module.</summary>
public sealed class SilUnitInfo {
    /// <summary>
    ///     The functions only this unit defines — its files' functions, their
    ///     closures and initialisers' halves. Everything else the module
    ///     defines, another unit may define as well.
    /// </summary>
    public HashSet<string> Owned { get; } = new(StringComparer.Ordinal);

    /// <summary>The functions other units define that this one may call or put in a vtable, with their signatures.</summary>
    public Dictionary<string, FunctionType> External { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     What this unit assumed about the program's types — each class's
    ///     superclass, deinit, fields and vtable, each struct's deinit — one
    ///     line per type. Every unit of a program must agree, or the units
    ///     cannot be linked.
    /// </summary>
    public SortedDictionary<string, string> Abi { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     For a unit that compiles a library's generics (<see cref="SilUnit.CompilesGenerics" />):
    ///     each generic function of its files it compiled once, by its name
    ///     without <c>$generic</c>, and whether that code describes values
    ///     (<see cref="CompiledGenerics.Functions" />).
    /// </summary>
    public SortedDictionary<string, bool> Generics { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Of <see cref="Generics" />, those other units may copy and
    ///     specialise: marked <c>@inline</c> or <c>@inlinable</c>, or small
    ///     (<see cref="SilGen.InlinableSize" />).
    /// </summary>
    public SortedSet<string> Inlinable { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Of <see cref="Generics" />, those that write a value of a type they
    ///     are given out with <c>?</c>: a caller makes that type's debug
    ///     describer, which it makes for no other.
    /// </summary>
    public SortedSet<string> DebugDescribing { get; } = new(StringComparer.Ordinal);
}

/// <summary>
///     The declarations lowering lowers, declares or checks, gathered once
///     from the compilation's symbols, so that each of many units' lowerings
///     does not look through the whole program again.
/// </summary>
/// <remarks>
///     <para>
///         A unit needs every declaration of the files it owns — those in
///         bodies too, a local function or class, which only its own syntax
///         holds — but of the other files only those another unit can see:
///         the declarations of their modules' and types' symbols, extensions'
///         members among them, and properties' accessors. Those are read from
///         the symbols the compilation declared rather than by walking the
///         other files' syntax (docs/05 §5.4), which is what lets a unit be
///         lowered against another unit's symbols alone.
///     </para>
///     <para>
///         Each file's declarations are in document order, as a walk of its
///         syntax would give them, so that what lowering names and makes does
///         not depend on how they were found.
///     </para>
/// </remarks>
public sealed class SilProgram {
    readonly Dictionary<SyntaxTree, IReadOnlyList<SyntaxNode>> _outline;
    readonly ConcurrentDictionary<SyntaxTree, IReadOnlyList<SyntaxNode>> _all = new();

    SilProgram(Dictionary<SyntaxTree, IReadOnlyList<SyntaxNode>> outline) {
        _outline = outline;
    }

    /// <summary>The declarations of the compilation's symbols that lowering looks at, each file's in document order.</summary>
    public static SilProgram Index(Compilation compilation) {
        var trees = new Dictionary<SyntaxNode, SyntaxTree>(ReferenceEqualityComparer.Instance);
        var found = new Dictionary<SyntaxTree, List<SyntaxNode>>();
        foreach (var tree in compilation.SyntaxTrees.Where(t => !compilation.IsFromInterface(t))) {
            trees.TryAdd(tree.Root, tree);
            found.TryAdd(tree, []);
        }

        var seen = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        foreach (var module in compilation.Modules) {
            foreach (var member in module.Members) {
                Visit(member);
            }
        }

        foreach (var carrier in compilation.ArrayCarriers) {
            Visit(carrier);
        }

        var outline = new Dictionary<SyntaxTree, IReadOnlyList<SyntaxNode>>();
        foreach (var (tree, declarations) in found) {
            declarations.Sort(static (a, b) => a.Span.Start.CompareTo(b.Span.Start));
            outline[tree] = declarations;
        }

        return new(outline);

        void Visit(Symbol symbol) {
            if (!visited.Add(symbol)) {
                return;
            }

            // A library taken from its interface says its own (Compilation.InterfaceDeclarations).
            if (compilation.IsTakenFromInterface(symbol)) {
                return;
            }

            foreach (var syntax in symbol.DeclaringSyntax) {
                Add(syntax);
            }

            if (symbol is NamedTypeSymbol type) {
                foreach (var member in type.OriginalDefinition.Members) {
                    Visit(member);
                }
            }
        }

        void Add(SyntaxNode syntax) {
            if (IsDeclaration(syntax) && seen.Add(syntax) && !InBody(syntax) && TreeOf(syntax) is { } tree) {
                found[tree].Add(syntax);
            }

            // A property's or a subscript's accessors are its own declaration's.
            var accessors = syntax switch {
                FieldDeclarationSyntax { Accessors: { } list } => list.Accessors,
                SubscriptDeclarationSyntax { Accessors: { } list } => list.Accessors,
                _ => default(SyntaxList<AccessorSyntax>?),
            };
            foreach (var accessor in accessors ?? []) {
                Add(accessor);
            }
        }

        SyntaxTree? TreeOf(SyntaxNode node) {
            var root = node;
            while (root.Parent is { } parent) {
                root = parent;
            }

            return trees.GetValueOrDefault(root);
        }
    }

    /// <summary>
    ///     Whether a declaration is inside a body — a function's, an
    ///     initialiser's, an accessor's, a closure's — where only its own
    ///     file's syntax reaches it.
    /// </summary>
    static bool InBody(SyntaxNode node) {
        for (var at = node.Parent; at is not null; at = at.Parent) {
            switch (at) {
                case CompilationUnitSyntax:
                    return false;
                case BlockSyntax or ExpressionSyntax:
                    return true;
            }
        }

        return false;
    }

    /// <summary>The nodes beneath one, in the order <see cref="SyntaxNode.DescendantNodes" /> gives — a level at a time, not through nested iterators.</summary>
    static void Walk(SyntaxNode node, List<SyntaxNode> declarations) {
        foreach (var child in node.ChildNodesAndTokens()) {
            if (child.AsNode() is { } inner) {
                if (IsDeclaration(inner)) {
                    declarations.Add(inner);
                }

                Walk(inner, declarations);
            }
        }
    }

    internal static bool IsDeclaration(SyntaxNode node) =>
        node is FunctionDeclarationSyntax or InitDeclarationSyntax or DeinitDeclarationSyntax or OperatorDeclarationSyntax
            or AccessorSyntax or SubscriptDeclarationSyntax or ClassDeclarationSyntax or FieldDeclarationSyntax or ConstDeclarationSyntax;

    /// <summary>A file's declarations: all of them, bodies' included, for a file the unit owns; its outline for another.</summary>
    internal IEnumerable<SyntaxNode>? Declarations(SyntaxTree tree, bool owned) {
        if (!_outline.TryGetValue(tree, out var outline)) {
            return null;
        }

        return owned
            ? _all.GetOrAdd(tree, static t => {
                var declarations = new List<SyntaxNode>();
                Walk(t.Root, declarations);
                return declarations;
            })
            : outline;
    }

    /// <summary>A file's outline: the declarations of its symbols another unit can see, in document order.</summary>
    public IReadOnlyList<SyntaxNode> Outline(SyntaxTree tree) => _outline.TryGetValue(tree, out var outline) ? outline : [];
}
