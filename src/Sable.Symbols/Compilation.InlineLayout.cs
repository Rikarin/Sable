using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>@inline_layout</c> (docs/02 §2.5): a struct whose bytes are its
///     serialised form conforms to <c>Sable.Core</c>'s <c>InlineLayout</c>.
/// </summary>
/// <remarks>
///     <para>
///         The protocol is Sable (<c>lib/Sable.Core/InlineLayout.sb</c>):
///         <c>static fn fromBytes(_ bytes: U8[&gt;..]) -&gt; Decoded&lt;Self&gt;</c>,
///         beside <c>Decoded</c> and <c>Bytes.of&lt;T: InlineLayout&gt;</c>.
///         The conformance is synthesised, as <c>@derive</c>'s are: once every
///         type is declared, the struct gets <c>fromBytes</c> — the
///         requirement's signature with <c>Self</c> the struct — and the
///         protocol in its base list. Only the body is the compiler's, written
///         by SIL lowering from the struct's layout: it validates framing,
///         every tag, every length and every relative link.
///     </para>
///     <para>
///         The attribute is how a type conforms; naming the protocol in a base
///         list without it is an error (<see cref="ConformanceChecker" />),
///         since no body could be written for a layout that is not inline.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    readonly List<(NamedTypeSymbol Type, AttributeSyntax Attribute)> _inlineLayouts = [];
    int _inlineLayoutsDeclared;

    /// <summary>Conforms every <c>@inline_layout</c> struct declared since the last call.</summary>
    void DeclareInlineLayouts() {
        while (_inlineLayoutsDeclared < _inlineLayouts.Count) {
            var (type, attribute) = _inlineLayouts[_inlineLayoutsDeclared++];
            DeclareInlineLayout(type, attribute);
        }
    }

    void DeclareInlineLayout(NamedTypeSymbol type, AttributeSyntax attribute) {
        if (Core.InlineLayout is not { SelfTypeParameter: { } selfParameter } protocol
            || protocol.GetMembers("fromBytes").OfType<FunctionSymbol>().FirstOrDefault() is not { } requirement) {
            ReportDeclaration(
                ErrorCode.ERR_TypeNotFound,
                attribute.Span,
                "'@inline_layout' needs Sable.Core's 'InlineLayout', which is not here"
            );
            return;
        }

        if (type.GetMembers("fromBytes").FirstOrDefault() is { } existing) {
            ReportDeclaration(
                ErrorCode.ERR_DuplicateDeclaration,
                existing.Location ?? attribute.Span,
                $"'{type.Name}' is '@inline_layout', so its 'fromBytes' is the compiler's, written for its layout; drop this one"
            );
            return;
        }

        var self = type.IsGenericDefinition ? type.Construct([..type.TypeParameters]) : type;
        var substitution = new TypeSubstitution(new() { [selfParameter] = self });

        var function = new FunctionSymbol(requirement.Name, type, null, Accessibility.Public, FunctionKind.Ordinary) {
            IsStatic = true,
        };

        function.Parameters = [
            ..requirement.Parameters.Select(ParameterSymbol (parameter, i) =>
                new(parameter.Name, parameter.Label, function, null, i) {
                    TypeResolver = () => substitution.Apply(parameter.Type),
                }
            ),
        ];

        function.ReturnTypeResolver = () => substitution.Apply(requirement.ReturnType);
        type.AddMember(function);

        // After the type's own base list is bound, which replaces the list.
        TypeBinder.Defer(() => {
                if (!type.BaseTypes.Contains(protocol)) {
                    type.BaseTypes = type.BaseTypes.Add(protocol);
                }
            }
        );
    }
}
