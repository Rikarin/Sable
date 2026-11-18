using System.Security.Cryptography;
using System.Text;
using Sable.Syntax;

namespace Sable.Driver.Build;

/// <summary>
///     A file's interface, as a fingerprint: everything in it that another
///     unit's compiled code can depend on — every declaration, signature,
///     field, initial value and generic body — and nothing that only its own
///     unit's code depends on: the bodies of ordinary functions. A change to
///     such a body leaves the fingerprint as it was, so that other units'
///     objects stay valid (docs/05 §5.4's <c>.sbi</c>, as a hash rather than
///     a file).
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
///         it, a protocol's defaults in the conforming types', a subscript or
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
public static class InterfaceFingerprint {
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
                    case TypealiasDeclarationSyntax alias: other.Add(alias.Identifier.Text); break;
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

    /// <summary>The fingerprint of one file's interface, given the program's <see cref="ConcreteTypeNames" />.</summary>
    public static string Of(SyntaxTree tree, IReadOnlySet<string> concreteTypes) {
        var writer = new Writer(tree, concreteTypes);
        writer.Node(tree.Root, positional: false);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(writer.Text.ToString())));
    }

    sealed class Writer(SyntaxTree tree, IReadOnlySet<string> concreteTypes) {
        public StringBuilder Text { get; } = new();

        public void Node(SyntaxNode node, bool positional, bool elide = true) {
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
                FunctionDeclarationSyntax { TypeParameters: null, Attributes.Count: 0 } f => (SyntaxNode?)f.Body ?? f.ExpressionBody,
                InitDeclarationSyntax { TypeParameters: null, Attributes.Count: 0 } i => i.Body,
                DeinitDeclarationSyntax { Attributes.Count: 0 } d => d.Body,
                OperatorDeclarationSyntax { Attributes.Count: 0 } o => (SyntaxNode?)o.Body ?? o.ExpressionBody,
                AccessorSyntax { Attributes.Count: 0 } a when a.Keyword.Kind == SyntaxKind.GetKeyword => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                _ => null,
            };
            if (body is null) {
                return null;
            }

            // Nothing in the signature that makes it generic or a reference.
            if (AnyToken(node, body, SyntaxKind.SomeKeyword, SyntaxKind.RefKeyword)) {
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
