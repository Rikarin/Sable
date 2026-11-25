using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

public sealed partial class SilGen {
    /// <summary>Where each instance of a generic function was asked for (<see cref="_instanceOrigins" />).</summary>
    readonly Dictionary<string, SyntaxNode> _requests = new(StringComparer.Ordinal);

    /// <summary>The first inline array too large to lower (S7004) since one was last reported, and its size.</summary>
    (ArrayTypeSymbol Array, long Bytes)? _tooLargeArray;

    /// <summary>
    ///     Reports what lowering has no code for (S7000). In the standard
    ///     library it says the function it is in, and is placed at the
    ///     program's own code that needs that function, where one generic
    ///     function led to another: what the library holds is not the
    ///     program's to change.
    /// </summary>
    void ReportNotLowered(SyntaxNode at, string what) {
        // What failed to lower because an inline array in it is too large
        // says so (S7004), where the program needs it.
        if (_tooLargeArray is var (array, bytes)) {
            _tooLargeArray = null;
            _diagnostics.Add(Diagnostic.Error(ErrorCode.ERR_InlineArrayTooLarge, at.Span,
                $"'{array.ToDisplayString()}' takes {bytes} bytes held inline, more than the {TypeLayout.MaximumInlineBytes} an inline array may: "
                + $"use '{array.ElementType.ToDisplayString()}{(array.ArrayKind == ArrayKind.Inline ? "[>..]" : "[]")}', whose elements are on the heap").AnchoredAt(at));
            return;
        }

        var message = $"SIL lowering does not handle {what} yet";
        var tree = _compilation.TreeOf(at);
        if (tree is null || !IsStandardLibrary(tree)) {
            _diagnostics.Add(Diagnostic.Error(ErrorCode.ERR_NotLoweredYet, at.Span, message).AnchoredAt(at));
            return;
        }

        var (line, column) = tree.Text.GetLinePosition(at.Span.Start);
        message += $", in '{EnclosingFunction(at)}' of the standard library ({tree.Text.FileName}:{line}:{column})";
        var (span, anchor) = (at.Span, at);
        for (var instance = _loweringInstance;
             instance is not null && _requests.TryGetValue(instance, out var request);
             instance = _instanceOrigins[instance].Requester) {
            if (_compilation.TreeOf(request) is { } requested && !IsStandardLibrary(requested)) {
                (span, anchor) = (request.Span, request);
                tree = requested;
                break;
            }
        }

        // What is only tried, a witness compiled once for one, reports nothing.
        var diagnostic = Diagnostic.Error(ErrorCode.ERR_NotLoweredYet, span, message).AnchoredAt(anchor);
        _diagnostics.Add(diagnostic);
        if (ReferenceEquals(_diagnostics.LastOrDefault(), diagnostic)) {
            _files.TryAdd(diagnostic, tree);
        }
    }

    /// <summary>A file of the standard library: the one the compiler carries, or the program's own standing in for it.</summary>
    static bool IsStandardLibrary(SyntaxTree tree) {
        var path = tree.Text.FileName.Replace('\\', '/');
        return path.StartsWith("lib/Sable.", StringComparison.Ordinal) || path.Contains("/lib/Sable.", StringComparison.Ordinal);
    }

    /// <summary>A function's name, or of one with none, a closure, where it is.</summary>
    static string NameOf(FunctionSymbol function) {
        for (Symbol? symbol = function; symbol is not null; symbol = symbol.ContainingSymbol) {
            if (symbol.Name.Length > 0) {
                return symbol.Name;
            }
        }

        return "a closure";
    }

    static string EnclosingFunction(SyntaxNode at) {
        for (var node = at; node is not null; node = node.Parent!) {
            switch (node) {
                case FunctionDeclarationSyntax function: return function.Identifier.Text;
                case InitDeclarationSyntax: return "init";
                case DeinitDeclarationSyntax: return "deinit";
                case SubscriptDeclarationSyntax: return "subscript";
                case DeclarationSyntax when node.DescendantTokens().FirstOrDefault(static t => t.Kind == SyntaxKind.IdentifierToken) is { IsNone: false } name:
                    return name.Text;
            }
        }

        return "the file";
    }
}
