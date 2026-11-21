using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sable.Syntax;

namespace Sable.Driver.Build;

/// <summary>
///     What a file shows other units, and what of theirs it may use: its
///     interface's <see cref="Fingerprint" />; its <see cref="Source" /> as
///     others see it; the fingerprint of its <see cref="Extensions" />, which
///     reach every unit; and the dotted names it writes
///     (<see cref="References" />), from which the units it depends on are found.
/// </summary>
/// <param name="Declarations">Each of its top-level declarations' own part of the interface (<see cref="DeclarationPrint" />), in order.</param>
/// <param name="Uses">The names its files write anywhere, bodies included: what of other units' declarations it may use.</param>
public sealed record FileInterface(
    string Fingerprint, string Source, string Extensions, IReadOnlyList<string> References,
    IReadOnlyList<DeclarationPrint> Declarations, IReadOnlyList<string> Uses);

/// <summary>
///     One top-level declaration's part of a file's interface — what a unit
///     using it can depend on (docs/05 §5.4, "Dependencies by name"): the name
///     it declares, the fingerprint of its interface (its members, signatures
///     and the bodies other units lower among it), and the names that
///     interface mentions, which a unit using it depends on in turn.
/// </summary>
/// <param name="Name">
///     The name declared; <see cref="Everyone" /> for a declaration that may
///     matter to a unit that never writes its name — an operator, a global,
///     a method declared on another type, anything not recognised.
/// </param>
public sealed record DeclarationPrint(string Name, string Fingerprint, IReadOnlyList<string> Mentions) {
    /// <summary>The name of a declaration every dependent unit depends on.</summary>
    public const string Everyone = "*";
}

/// <summary>
///     A file's interface, as a fingerprint: everything in it that another
///     unit's compiled code can depend on — every declaration, signature,
///     field, initial value and generic body — and nothing that only its own
///     unit's code depends on: the bodies of ordinary functions. A change to
///     such a body leaves the fingerprint as it was, so that other units'
///     objects stay valid (docs/05 §5.4's <c>.sbi</c>: the hash, and the
///     source it covers, are what <see cref="InterfaceFile" /> keeps).
/// </summary>
/// <remarks>
///     <para>
///         A body is left out only where it is certain that it is lowered in
///         its own unit and nowhere else: a function, initialiser, deinit,
///         operator or property getter with no type parameters, no
///         <c>some</c> and no <c>ref</c> in its signature and no attribute,
///         at module level or in a struct, class, union, enum or actor with
///         no type parameters — or in an extension of a type that the program
///         declares, and declares only as such a type. Everything else counts
///         as interface: a generic body is instantiated in the units that use
///         it, a <c>const fn</c>'s run by the compiler for the units whose
///         constants call it, a protocol's defaults in the conforming types', a subscript or
///         setter where it is used, and anything unrecognised is kept.
///     </para>
///     <para>
///         Where code is lowered into another unit, its position matters too —
///         a trap's message and the debug information carry the line — so
///         the tokens of a body that is kept, of a declaration with an
///         initial value and of a signature with a default argument are
///         fingerprinted with their line and column. Others are not, so that
///         a body growing by a line does not move every declaration after it.
///     </para>
/// </remarks>
public static partial class InterfaceFingerprint {
    /// <summary>
    ///     The simple names of the types whose extensions may hold bodies that
    ///     are not interface: declared, somewhere in the program, as a
    ///     non-generic struct, class, union, enum or actor, and as nothing else.
    /// </summary>
    public static IReadOnlySet<string> ConcreteTypeNames(IEnumerable<SyntaxTree> trees) {
        var concrete = new HashSet<string>(StringComparer.Ordinal);
        var other = new HashSet<string>(StringComparer.Ordinal);
        // Types are declared at the top of a file or inside other types: their
        // members are walked, not every node of every body.
        var pending = new Stack<SyntaxNode>();
        foreach (var tree in trees) {
            if (tree.Root is CompilationUnitSyntax unit) {
                foreach (var member in unit.Members) {
                    pending.Push(member);
                }
            }

            while (pending.TryPop(out var node)) {
                foreach (var member in MembersOf(node)) {
                    pending.Push(member);
                }

                switch (node) {
                    case StructDeclarationSyntax { TypeParameters: null } s: concrete.Add(s.Identifier.Text); break;
                    case ClassDeclarationSyntax { TypeParameters: null } c: concrete.Add(c.Identifier.Text); break;
                    case UnionDeclarationSyntax { TypeParameters: null } u: concrete.Add(u.Identifier.Text); break;
                    case ActorDeclarationSyntax { TypeParameters: null } a: concrete.Add(a.Identifier.Text); break;
                    case EnumDeclarationSyntax e: concrete.Add(e.Identifier.Text); break;
                    case StructDeclarationSyntax s: other.Add(s.Identifier.Text); break;
                    case ClassDeclarationSyntax c: other.Add(c.Identifier.Text); break;
                    case UnionDeclarationSyntax u: other.Add(u.Identifier.Text); break;
                    case ActorDeclarationSyntax a: other.Add(a.Identifier.Text); break;
                    case ProtocolDeclarationSyntax p: other.Add(p.Identifier.Text); break;
                    case AliasDeclarationSyntax alias: other.Add(alias.Identifier.Text); break;
                    case AssociatedTypeDeclarationSyntax associated: other.Add(associated.Identifier.Text); break;
                }
            }
        }

        concrete.ExceptWith(other);
        return concrete;
    }

    static IEnumerable<SyntaxNode> MembersOf(SyntaxNode node) =>
        node switch {
            StructDeclarationSyntax s => s.Members,
            ClassDeclarationSyntax c => c.Members,
            ActorDeclarationSyntax a => a.Members,
            ProtocolDeclarationSyntax p => p.Members,
            UnionDeclarationSyntax u => u.Members,
            EnumDeclarationSyntax e => e.Members,
            ExtensionDeclarationSyntax x => x.Members,
            _ => [],
        };

    /// <summary>Whether a token under the node, outside <paramref name="except" />, is of one of the kinds — walked a level at a time.</summary>
    static bool AnyToken(SyntaxNode node, SyntaxNode? except, SyntaxKind kind, SyntaxKind other = SyntaxKind.None) {
        foreach (var child in node.ChildNodesAndTokens()) {
            if (child.IsToken) {
                if (child.Kind == kind || child.Kind == other && other != SyntaxKind.None) {
                    return true;
                }
            } else if (child.AsNode() is { } inner && !Same(inner, except) && AnyToken(inner, null, kind, other)) {
                return true;
            }
        }

        return false;
    }

    static bool Same(SyntaxNode node, SyntaxNode? other) => other is not null && node.Kind == other.Kind && node.Span == other.Span;

    /// <summary>
    ///     The simple names of the types a file's extensions extend: all that
    ///     the program's <see cref="ConcreteTypeNames" /> can change of its interface.
    /// </summary>
    public static IEnumerable<string> ExtendedNames(SyntaxTree tree) {
        var pending = new Stack<SyntaxNode>(tree.Root is CompilationUnitSyntax unit ? unit.Members : []);
        while (pending.TryPop(out var node)) {
            foreach (var member in MembersOf(node)) {
                pending.Push(member);
            }

            if (node is ExtensionDeclarationSyntax extension && LastName(extension) is { } name) {
                yield return name;
            }
        }
    }

    static string? LastName(ExtensionDeclarationSyntax extension) {
        string? last = null;
        foreach (var token in extension.Type.DescendantTokens()) {
            last = token.Kind == SyntaxKind.IdentifierToken ? token.Text : last;
        }

        return last;
    }

    /// <summary>The fingerprint of one file's interface, given the program's <see cref="ConcreteTypeNames" />.</summary>
    public static string Of(SyntaxTree tree, IReadOnlySet<string> concreteTypes) => Interface(tree, concreteTypes).Fingerprint;

    /// <summary>A name with a dot in it, which may name a module or something in one.</summary>
    [GeneratedRegex(@"(?<![\w.])[\p{L}_][\p{L}\p{Nd}_]*(?:\.[\p{L}_][\p{L}\p{Nd}_]*)+")]
    private static partial Regex DottedName();

    /// <summary>
    ///     One file's interface: its fingerprint, and its source as another
    ///     unit sees it — the file with each body that is not interface
    ///     blanked (<see cref="Blank" />), what a <c>.sbi</c> file holds
    ///     (<see cref="InterfaceFile" />) and other units bind against —
    ///     the fingerprint of its extensions, and the dotted names it writes.
    /// </summary>
    /// <remarks>
    ///     The dotted names are looked for in the text, bodies and comments
    ///     included: a module can be named in full without an import
    ///     (<c>Sable.Math.sqrt(x)</c>), and a name found that is not a
    ///     module's costs only a dependency that is not there.
    /// </remarks>
    public static FileInterface Interface(SyntaxTree tree, IReadOnlySet<string> concreteTypes) {
        var writer = new Writer(tree, concreteTypes);
        writer.Node(tree.Root, positional: false);
        var text = tree.Text.Text;
        var source = new StringBuilder(text);
        foreach (var body in writer.Elided) {
            Blank(source, body);
        }

        var references = DottedName().Matches(text).Select(static m => m.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // Each declaration on its own, for the units that use it by name;
        // extensions reach every unit through the extensions' fingerprint.
        var declarations = new List<DeclarationPrint>();
        if (tree.Root is CompilationUnitSyntax unit) {
            foreach (var member in unit.Members) {
                if (member is ExtensionDeclarationSyntax) {
                    continue;
                }

                var one = new Writer(tree, concreteTypes);
                one.Node(member, positional: false);
                declarations.Add(new(DeclaredName(member), Hash(one.Text.ToString()), [..one.Mentions.Order(StringComparer.Ordinal)]));
            }
        }

        var uses = tree.Root.DescendantTokens().Where(static t => t.Kind == SyntaxKind.IdentifierToken).Select(static t => t.Text)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new(Hash(writer.Text.ToString()), source.ToString(), Hash(writer.Extensions.ToString()), references, declarations, uses);
    }

    /// <summary>
    ///     The name a top-level declaration gives what it declares, by which
    ///     a unit using it is found; <see cref="DeclarationPrint.Everyone" />
    ///     for one that matters to units that do not write a name of it.
    /// </summary>
    static string DeclaredName(SyntaxNode member) =>
        member switch {
            // 'fn Type.method' adds to a type, as an extension does.
            FunctionDeclarationSyntax f when f.DotToken.IsNone && f.Identifier.Kind == SyntaxKind.IdentifierToken => f.Identifier.Text,
            StructDeclarationSyntax s => s.Identifier.Text,
            ClassDeclarationSyntax c => c.Identifier.Text,
            UnionDeclarationSyntax u => u.Identifier.Text,
            EnumDeclarationSyntax e => e.Identifier.Text,
            ActorDeclarationSyntax a => a.Identifier.Text,
            ProtocolDeclarationSyntax p => p.Identifier.Text,
            AliasDeclarationSyntax alias => alias.Identifier.Text,
            _ => DeclarationPrint.Everyone,
        };

    /// <summary>
    ///     A body left out of a file's source as other units see it: a block
    ///     becomes an empty one, an expression <c>0</c>, and the rest spaces —
    ///     with its line breaks kept, so that everything after it is where it
    ///     was, and what other units lower from the file (a generic body, a
    ///     default) has the lines and columns it has in the file.
    /// </summary>
    static void Blank(StringBuilder source, TextSpan body) {
        var block = source[body.Start] == '{';
        for (var i = body.Start; i < body.End; i++) {
            if (source[i] is not ('\n' or '\r')) {
                source[i] = ' ';
            }
        }

        if (block) {
            source[body.Start] = '{';
            source[body.End - 1] = '}';
        } else {
            source[body.Start] = '0';
        }
    }

    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    sealed class Writer(SyntaxTree tree, IReadOnlySet<string> concreteTypes) {
        public StringBuilder Text { get; } = new();

        /// <summary>The names the interface written mentions.</summary>
        public HashSet<string> Mentions { get; } = new(StringComparer.Ordinal);

        /// <summary>The spans of the bodies left out, in document order.</summary>
        public List<TextSpan> Elided { get; } = [];

        /// <summary>
        ///     The extensions' part of <see cref="Text" />. An extension adds to
        ///     a type that units which do not depend on it can see — a
        ///     conformance, an overload that changes how the type's methods
        ///     are named — so every unit is keyed by every extension.
        /// </summary>
        public StringBuilder Extensions { get; } = new();

        public void Node(SyntaxNode node, bool positional, bool elide = true) {
            if (node is not ExtensionDeclarationSyntax) {
                Write(node, positional, elide);
                return;
            }

            var from = Text.Length;
            Write(node, positional, elide);
            Extensions.Append(Text, from, Text.Length - from);
        }

        void Write(SyntaxNode node, bool positional, bool elide) {
            if (elide && !positional && Body(node) is { } body) {
                // The declaration without its body: the body is its unit's own.
                // A default argument is code too, made at each call.
                var defaults = AnyToken(node, body, SyntaxKind.EqualsToken);
                foreach (var child in node.ChildNodesAndTokens()) {
                    if (child.IsToken) {
                        Token(child.AsToken(), defaults);
                    } else if (child.AsNode() is { } inner) {
                        if (Same(inner, body)) {
                            Text.Append("{…}\n");
                            Elided.Add(body.Span);
                        } else {
                            Node(inner, positional: defaults, elide: false);
                        }
                    }
                }

                return;
            }

            if (!positional && IsFunctionLike(node)) {
                positional = true;
            }

            // An initial value is code, lowered wherever the value is made.
            if (!positional && node is FieldDeclarationSyntax or ConstDeclarationSyntax or UnionCaseDeclarationSyntax or EnumCaseDeclarationSyntax
                && AnyToken(node, null, SyntaxKind.EqualsToken)) {
                positional = true;
            }

            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.IsToken) {
                    Token(child.AsToken(), positional);
                } else if (child.AsNode() is { } inner) {
                    Node(inner, positional, elide);
                }
            }
        }

        void Token(SyntaxToken token, bool positional) {
            if (token.Kind == SyntaxKind.IdentifierToken) {
                Mentions.Add(token.Text);
            }

            Text.Append((int)token.Kind).Append(' ').Append(token.Text);
            if (positional) {
                var position = tree.Text.GetLinePosition(token.SpanStart);
                Text.Append(" @").Append(position.Line).Append(':').Append(position.Column);
            }

            Text.Append('\n');
        }

        static bool IsFunctionLike(SyntaxNode node) =>
            node is FunctionDeclarationSyntax or InitDeclarationSyntax or DeinitDeclarationSyntax or OperatorDeclarationSyntax
                or AccessorSyntax or SubscriptDeclarationSyntax;

        /// <summary>The body of a declaration whose body is not interface, or null.</summary>
        SyntaxNode? Body(SyntaxNode node) {
            SyntaxNode? body = node switch {
                // A 'const fn' runs wherever a constant calls it, while that unit compiles.
                FunctionDeclarationSyntax { TypeParameters: null, Attributes.Count: 0 } f when !f.Modifiers.Any(SyntaxKind.ConstKeyword) => (SyntaxNode?)f.Body ?? f.ExpressionBody,
                InitDeclarationSyntax { TypeParameters: null, Attributes.Count: 0 } i => i.Body,
                DeinitDeclarationSyntax { Attributes.Count: 0 } d => d.Body,
                OperatorDeclarationSyntax { Attributes.Count: 0 } o => (SyntaxNode?)o.Body ?? o.ExpressionBody,
                AccessorSyntax { Attributes.Count: 0 } a when a.Keyword.Kind == SyntaxKind.GetKeyword => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                _ => null,
            };
            if (body is null) {
                return null;
            }

            // Nothing in the signature that makes it generic or a reference
            // ('T&'; an '&' that is something else only errs on the safe side).
            if (AnyToken(node, body, SyntaxKind.SomeKeyword, SyntaxKind.AmpersandToken)) {
                return null;
            }

            // A getter's property: a stored-or-computed member of a type, not static.
            var container = node.Parent;
            if (node is AccessorSyntax) {
                if (container is not AccessorListSyntax { Parent: FieldDeclarationSyntax field }
                    || field.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword)
                    || field.Parent is CompilationUnitSyntax) {
                    return null;
                }

                container = field.Parent;
            }

            return container switch {
                CompilationUnitSyntax => body,
                StructDeclarationSyntax { TypeParameters: null } s when ConcreteChain(s.Parent) => body,
                ClassDeclarationSyntax { TypeParameters: null } c when ConcreteChain(c.Parent) => body,
                UnionDeclarationSyntax { TypeParameters: null } u when ConcreteChain(u.Parent) => body,
                ActorDeclarationSyntax { TypeParameters: null } a when ConcreteChain(a.Parent) => body,
                EnumDeclarationSyntax e when ConcreteChain(e.Parent) => body,
                ExtensionDeclarationSyntax { WhereClause: null } extension when ConcreteChain(extension.Parent) && ExtendsConcrete(extension) => body,
                _ => null,
            };
        }

        static void Collect(SyntaxNode node, List<SyntaxToken> tokens) {
            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.IsToken) {
                    tokens.Add(child.AsToken());
                } else if (child.AsNode() is { } inner) {
                    Collect(inner, tokens);
                }
            }
        }

        /// <summary>Whether a type's enclosing declarations, if any, are as concrete as it is.</summary>
        bool ConcreteChain(SyntaxNode? parent) =>
            parent switch {
                CompilationUnitSyntax => true,
                StructDeclarationSyntax { TypeParameters: null } s => ConcreteChain(s.Parent),
                ClassDeclarationSyntax { TypeParameters: null } c => ConcreteChain(c.Parent),
                UnionDeclarationSyntax { TypeParameters: null } u => ConcreteChain(u.Parent),
                ActorDeclarationSyntax { TypeParameters: null } a => ConcreteChain(a.Parent),
                EnumDeclarationSyntax e => ConcreteChain(e.Parent),
                _ => false,
            };

        /// <summary>An extension of a plain name (or a dotted one) that the program declares only as a concrete type.</summary>
        bool ExtendsConcrete(ExtensionDeclarationSyntax extension) {
            var tokens = new List<SyntaxToken>();
            Collect(extension.Type, tokens);
            return tokens.Count > 0
                && tokens.All(static t => t.Kind is SyntaxKind.IdentifierToken or SyntaxKind.DotToken)
                && tokens[^1].Kind == SyntaxKind.IdentifierToken
                && concreteTypes.Contains(tokens[^1].Text);
        }
    }
}
