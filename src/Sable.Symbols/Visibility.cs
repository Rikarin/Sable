using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Where a declaration is visible from, per spec/SYNTAX.md §2: public
///     everywhere, package across the workspace, internal within the module,
///     private within the enclosing declaration — which, for one at the top
///     level, is its file.
/// </summary>
/// <remarks>
///     A member an extension declares belongs to the extended type, but its
///     module is the extension's: <c>extension String { fn twice() }</c> in
///     an app gives String an internal member of the app, not of the core.
/// </remarks>
internal static class Visibility {
    /// <summary>The module that decides who sees a symbol: its own, or its extension's.</summary>
    public static ModuleSymbol? ModuleOf(Symbol symbol) {
        for (var here = symbol; here is not null; here = here.ContainingSymbol) {
            switch (here) {
                case ExtensionSymbol { DeclaringModule: { } module }:
                    return module;

                case ModuleSymbol module:
                    return module;
            }
        }

        return null;
    }

    /// <summary>The syntax a symbol was written as, or its container's when the compiler made it.</summary>
    static SyntaxNode? DeclarationOf(Symbol symbol) {
        for (var here = symbol; here is not null; here = here.ContainingSymbol) {
            if (!here.DeclaringSyntax.IsEmpty) {
                return here.DeclaringSyntax[0];
            }
        }

        return null;
    }

    static SyntaxNode RootOf(SyntaxNode node) {
        while (node.Parent is { } parent) {
            node = parent;
        }

        return node;
    }

    /// <summary>
    ///     True when <paramref name="symbol" /> may be seen from a file, for the
    ///     levels that do not depend on where in the file: everything except a
    ///     private member of a type, which the binder decides by the
    ///     declarations around the use.
    /// </summary>
    public static bool IsVisible(Symbol symbol, Accessibility accessibility, FileScope? scope, Compilation compilation) {
        if (scope is null) {
            return true;
        }

        switch (accessibility) {
            case Accessibility.Public:
                return true;

            // The workspace a file belongs to is part of how it was parsed;
            // the files of one workspace see each other's package members.
            case Accessibility.Package:
                return DeclarationOf(symbol) is not { } written
                    || compilation.FileScopeOf(written) is not { } declared
                    || string.Equals(declared.Tree.Options.Workspace, scope.Tree.Options.Workspace, StringComparison.Ordinal);

            case Accessibility.Internal:
                return ModuleOf(symbol) is not { } module || ReferenceEquals(module, scope.Module);

            // At the top level the enclosing declaration is the file.
            default:
                return symbol.ContainingSymbol is not ModuleSymbol
                    || DeclarationOf(symbol) is not { } declaration
                    || ReferenceEquals(RootOf(declaration), scope.Tree.Root);
        }
    }

    public static bool IsVisible(Symbol symbol, FileScope? scope, Compilation compilation) =>
        IsVisible(symbol, symbol.DeclaredAccessibility, scope, compilation);

    /// <summary>How an access level is named in a diagnostic.</summary>
    public static string Describe(Symbol symbol, Accessibility accessibility) =>
        accessibility switch {
            Accessibility.Private when symbol.ContainingSymbol is ModuleSymbol => "private to its file",
            Accessibility.Private => $"private to '{OwnerName(symbol)}'",
            Accessibility.Internal => $"internal to '{ModuleOf(symbol)?.Name ?? OwnerName(symbol)}'",
            Accessibility.Package => "visible only within its workspace",
            _ => "visible",
        };

    static string OwnerName(Symbol symbol) =>
        symbol.ContainingSymbol switch {
            ExtensionSymbol extension => extension.ExtendedType.Name,
            { } container => container.Name,
            null => "its module",
        };
}
