using Sable.Syntax;

namespace Sable.Driver.Build;

/// <summary>
///     What a unit declares and names of the program's modules, from which
///     the units it depends on are found (<see cref="UnitGraph" />).
/// </summary>
/// <param name="Modules">The modules its files declare.</param>
/// <param name="Imports">The modules its files import, by the names written.</param>
/// <param name="References">The dotted names its files write, any of which may start with a module's name.</param>
public sealed record UnitFacts(IReadOnlySet<string> Modules, IReadOnlyCollection<string> Imports, IReadOnlyCollection<string> References) {
    /// <summary>The modules a file declares and imports, read from its first lines.</summary>
    public static (string? Module, IEnumerable<string> Imports) Of(SyntaxTree tree) =>
        tree.Root is CompilationUnitSyntax unit
            ? (unit.Module is { } module ? Name(module.Name) : null, unit.Imports.Select(static i => Name(i.Name)))
            : (null, []);

    static string Name(SyntaxNode name) => string.Concat(name.DescendantTokens().Select(static t => t.Text));
}

/// <summary>
///     Which of a program's units each depends on (docs/05 §5.4): a unit's
///     object is keyed by its own files and the interfaces of these, so a
///     change to a unit's interface compiles again the units that depend on
///     it, and no others.
/// </summary>
/// <remarks>
///     A unit depends on the units declaring a module it imports, or names
///     in full (<c>Shapes.Square</c> needs no import), and on those its
///     modules are shared with — what is declared in a module, another unit
///     of it sees as its own: a subclass of a class that is not open, an
///     overload of a function — and then on what those depend on. The unit
///     holding the entry point depends on every unit, since it sets up every
///     global. The standard library is not one of them: every unit depends
///     on it, and it on none.
/// </remarks>
public static class UnitGraph {
    /// <summary>For each unit, the others it depends on, directly or not, in order; the first unit is the program's entry.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Dependencies(IReadOnlyList<UnitFacts> units) {
        var declaring = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < units.Count; i++) {
            foreach (var module in units[i].Modules) {
                if (!declaring.TryGetValue(module, out var list)) {
                    declaring[module] = list = [];
                }

                list.Add(i);
            }
        }

        var direct = new HashSet<int>[units.Count];
        for (var i = 0; i < units.Count; i++) {
            var found = direct[i] = [];
            void Add(string module) {
                if (declaring.TryGetValue(module, out var list)) {
                    found.UnionWith(list);
                }
            }

            foreach (var module in units[i].Modules) {
                Add(module);
            }

            // An import names a module, or the start of one's name: an alias
            // of 'Sable' reaches 'Sable.Math' as 'S.Math'.
            foreach (var import in units[i].Imports) {
                Add(import);
                foreach (var (module, _) in declaring) {
                    if (module.StartsWith(import + ".", StringComparison.Ordinal)) {
                        Add(module);
                    }
                }
            }

            // A dotted name may start with a module's: each of its prefixes.
            foreach (var reference in units[i].References) {
                for (var dot = reference.IndexOf('.'); dot >= 0; dot = reference.IndexOf('.', dot + 1)) {
                    Add(reference[..dot]);
                }

                Add(reference);
            }

            found.Remove(i);
        }

        if (units.Count > 0) {
            direct[0].UnionWith(Enumerable.Range(1, units.Count - 1));
        }

        // What each depends on, directly or through another.
        var closure = new List<IReadOnlyList<int>>();
        for (var i = 0; i < units.Count; i++) {
            var seen = new HashSet<int>();
            var pending = new Stack<int>(direct[i]);
            while (pending.TryPop(out var next)) {
                if (next != i && seen.Add(next)) {
                    foreach (var further in direct[next]) {
                        pending.Push(further);
                    }
                }
            }

            closure.Add([..seen.Order()]);
        }

        return closure;
    }
}
