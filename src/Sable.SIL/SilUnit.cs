using System.Collections.Concurrent;
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
public sealed record SilUnit(Func<SyntaxTree, bool> Owns);

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
}

/// <summary>
///     What lowering finds by walking every file of the program — the
///     declarations it lowers, declares or checks — gathered once, so that
///     each of many units' lowerings does not walk the whole program again.
/// </summary>
/// <remarks>
///     A unit needs every declaration of the files it owns, but of the other
///     files only those another unit can see: the ones outside function
///     bodies. Those are found without visiting a body, which is most of a file.
/// </remarks>
public sealed class SilProgram {
    readonly Dictionary<SyntaxTree, IReadOnlyList<SyntaxNode>> _outline;
    readonly ConcurrentDictionary<SyntaxTree, IReadOnlyList<SyntaxNode>> _all = new();

    SilProgram(Dictionary<SyntaxTree, IReadOnlyList<SyntaxNode>> outline) {
        _outline = outline;
    }

    /// <summary>The program's files' declarations outside bodies, of the kinds lowering looks at, each file's in document order.</summary>
    public static SilProgram Index(IEnumerable<SyntaxTree> trees) {
        var all = trees.Distinct().ToList();
        var found = new IReadOnlyList<SyntaxNode>[all.Count];
        Parallel.For(0, all.Count, i => {
            var declarations = new List<SyntaxNode>();
            Outline(all[i].Root, declarations);
            found[i] = declarations;
        });
        var outline = new Dictionary<SyntaxTree, IReadOnlyList<SyntaxNode>>();
        for (var i = 0; i < all.Count; i++) {
            outline[all[i]] = found[i];
        }

        return new(outline);
    }

    /// <summary>
    ///     The declarations of a file another unit can see — members of the
    ///     file and of its types, and properties' accessors — in document order.
    /// </summary>
    static void Outline(SyntaxNode node, List<SyntaxNode> declarations) {
        IEnumerable<SyntaxNode> members = node switch {
            CompilationUnitSyntax unit => unit.Members,
            StructDeclarationSyntax s => s.Members,
            ClassDeclarationSyntax c => c.Members,
            ActorDeclarationSyntax a => a.Members,
            ProtocolDeclarationSyntax p => p.Members,
            UnionDeclarationSyntax u => u.Members,
            EnumDeclarationSyntax e => e.Members,
            ExtensionDeclarationSyntax x => x.Members,
            FieldDeclarationSyntax { Accessors: { } accessors } => accessors.Accessors,
            SubscriptDeclarationSyntax { Accessors: { } accessors } => accessors.Accessors,
            _ => [],
        };
        foreach (var member in members) {
            if (IsDeclaration(member)) {
                declarations.Add(member);
            }

            Outline(member, declarations);
        }
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
            or AccessorSyntax or ClassDeclarationSyntax or FieldDeclarationSyntax or ConstDeclarationSyntax;

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
}
