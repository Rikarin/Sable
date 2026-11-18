using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     The public names of every library module, compiled once. A package's
///     compilation holds only the library modules its files import, so a
///     name from one no file imports yet is found here: completion offers
///     it with the import it needs, and a name not found gets a quick fix
///     that adds that import.
/// </summary>
/// <remarks>
///     The library does not change while the server runs, so this is built
///     once, in the background, the first time anything asks; every
///     signature is bound then, so its symbols are only read afterwards.
/// </remarks>
internal sealed class LibraryIndex(IReadOnlyCollection<string> modules, Func<SyntaxTree, IReadOnlyList<SyntaxTree>> libraryFor, Action<string> log) {
    /// <summary>A public type, function or alias of a library module.</summary>
    public sealed record Entry(string Module, Symbol Symbol);

    readonly Lazy<Task<IReadOnlyList<Entry>>> _entries = new(() => Task.Run(() => Build(modules, libraryFor, log)));

    /// <summary>The names, once built; starts building them, and is null until they are.</summary>
    public IReadOnlyList<Entry>? Ready {
        get {
            var task = _entries.Value;
            return task.IsCompletedSuccessfully ? task.Result : null;
        }
    }

    /// <summary>The names, waiting for them to be built.</summary>
    public Task<IReadOnlyList<Entry>> GetAsync() => _entries.Value;

    static IReadOnlyList<Entry> Build(IReadOnlyCollection<string> modules, Func<SyntaxTree, IReadOnlyList<SyntaxTree>> libraryFor, Action<string> log) {
        if (modules.Count == 0) {
            return [];
        }

        try {
            var text = "module SableLibraryIndex\n\n" + string.Concat(modules.Order(StringComparer.Ordinal).Select(static m => $"import {m}\n"));
            var tree = SyntaxTree.Parse(new SourceText(text, "<library index>"));
            var compilation = Compilation.Create([tree, ..libraryFor(tree)]);
            compilation.GetDiagnostics();

            var wanted = modules.ToHashSet(StringComparer.Ordinal);
            var entries = new List<Entry>();
            foreach (var module in compilation.Modules.OrderBy(static m => m.Name, StringComparer.Ordinal)) {
                if (!wanted.Contains(module.Name) || module.Name == CoreLibrary.ModuleName) {
                    continue;
                }

                foreach (var member in module.Members) {
                    if (member is NamedTypeSymbol or FunctionSymbol { FunctionKind: FunctionKind.Ordinary } or AliasSymbol
                        && member.DeclaredAccessibility == Accessibility.Public) {
                        // Bound now, while nothing else reads the compilation.
                        _ = SymbolDisplay.Signature(member);
                        entries.Add(new(module.Name, member));
                    }
                }
            }

            return entries;
        } catch (Exception e) when (e is not OutOfMemoryException) {
            log($"indexing the library failed: {e.Message}");
            return [];
        }
    }
}
