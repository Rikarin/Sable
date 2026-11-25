using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     The order globals and statics are given their initial values in: each
///     after the ones it reads, so that none reads one not yet set (which
///     would read zero, or follow a null reference).
/// </summary>
/// <remarks>
///     <para>
///         What an initial value reads is what its expression names and what
///         the functions it calls read, as Go orders its package variables. A
///         call through a protocol, or of a method a subclass overrides, goes
///         where only running it says: it may be any of the program's types'
///         own, so a global one of them reads comes first too, where that
///         leaves an order — and is no reason for an error where it does
///         not. The standard library's globals come before the program's.
///         Globals that need nothing of each other keep the order they were
///         declared in, file by file, so the program is set up as it reads
///         where it can be.
///     </para>
///     <para>
///         Two globals that need each other, through any number of others or
///         functions, have no order that works, and are an error (S3411); so
///         is one that needs itself.
///     </para>
/// </remarks>
public sealed partial class SilGen {
    /// <summary>The globals, in the order their initial values are stored.</summary>
    List<(FieldSymbol Field, string Name, SemanticModel Model)> OrderGlobals() {
        var declared = new Dictionary<FieldSymbol, int>();
        foreach (var (global, i) in _globalFields.Select(static (g, i) => (g, i))) {
            declared[global.Field] = i;
        }

        var ordered = new List<(FieldSymbol Field, string Name, SemanticModel Model)>(_globalFields.Count);
        var state = new Dictionary<FieldSymbol, bool>(); // false while being visited, true once placed
        var path = new List<(FieldSymbol Global, IReadOnlyList<FunctionSymbol> Through, bool Maybe)>();
        var reported = new HashSet<FieldSymbol>();

        void Visit(int index) {
            var global = _globalFields[index];
            state[global.Field] = false;
            foreach (var (needed, through, maybe) in GlobalsNeededBy(global.Field, global.Model).OrderBy(static n => n.Maybe).ThenBy(n => declared[n.Global])) {
                path.Add((global.Field, through, maybe));
                if (!state.TryGetValue(needed, out var placed)) {
                    Visit(declared[needed]);
                } else if (!placed && path.SkipWhile(p => !ReferenceEquals(p.Global, needed)).ToList() is var cycle && !cycle.Exists(static p => p.Maybe)) {
                    ReportCycle(cycle.ConvertAll(static p => (p.Global, p.Through)), reported);
                }

                path.RemoveAt(path.Count - 1);
            }

            state[global.Field] = true;
            ordered.Add(global);
        }

        foreach (var library in (bool[])[true, false]) {
            for (var i = 0; i < _globalFields.Count; i++) {
                if (!state.ContainsKey(_globalFields[i].Field) && CoreLibrary.IsStandardLibrary(_globalFields[i].Model.SyntaxTree.Header.Module) == library) {
                    Visit(i);
                }
            }
        }

        return ordered;
    }

    /// <summary>
    ///     The globals a global's initial value reads, each with the functions
    ///     it reads it through, the first way found, and whether only a call
    ///     that may go elsewhere reads it.
    /// </summary>
    List<(FieldSymbol Global, IReadOnlyList<FunctionSymbol> Through, bool Maybe)> GlobalsNeededBy(FieldSymbol field, SemanticModel model) {
        var needed = new List<(FieldSymbol, IReadOnlyList<FunctionSymbol>, bool)>();
        if (model.GetFieldInitializer(field) is not { } initial) {
            return needed;
        }

        // Breadth first, so the way through functions each is named by is
        // the shortest.
        var seenGlobals = new HashSet<FieldSymbol>();
        var seenFunctions = new HashSet<FunctionSymbol>();
        var pending = new Queue<(References References, IReadOnlyList<FunctionSymbol> Through)>();
        var maybe = new List<(FunctionSymbol Function, IReadOnlyList<FunctionSymbol> Through)>();
        pending.Enqueue((ReferencesOf([initial]), []));
        for (var certain = true; pending.Count > 0 || maybe.Count > 0; certain = false) {
            // What may be called, once all that is has been followed.
            foreach (var (function, through) in maybe) {
                if (seenFunctions.Add(function)) {
                    pending.Enqueue((ReferencesOf(function), [..through, function]));
                }
            }

            maybe.Clear();
            while (pending.Count > 0) {
                var ((globals, functions, mayRun), through) = pending.Dequeue();
                maybe.AddRange(mayRun.Select(m => (m, through)));
                foreach (var global in globals) {
                    if (seenGlobals.Add(global)) {
                        needed.Add((global, through, !certain));
                    }
                }

                foreach (var function in functions) {
                    if (seenFunctions.Add(function)) {
                        pending.Enqueue((ReferencesOf(function), [..through, function]));
                        maybe.AddRange(MayRun(function, function.Name, function.Parameters.Length).Select(m => (m, through)));
                    }
                }
            }
        }

        return needed;
    }

    Dictionary<string, List<FunctionSymbol>>? _programMembers;

    /// <summary>
    ///     The program's own functions a call of this one may run in its place:
    ///     the members its types meet a requirement with, or override a method
    ///     with. The library's are not looked through: its globals come first.
    /// </summary>
    IEnumerable<FunctionSymbol> MayRun(Symbol member, string name, int parameters) {
        var requirement = member is FunctionSymbol called ? RequirementProtocol(called) : member.ContainingSymbol as NamedTypeSymbol is { TypeKind: TypeKind.Protocol } declaring ? declaring : null;
        var overridden = requirement is null && member is FunctionSymbol { IsOpen: true } or FunctionSymbol { IsAbstract: true } ? Owner((FunctionSymbol)member)?.OriginalDefinition : null;
        if (requirement is null && overridden is not { TypeKind: TypeKind.Class }) {
            return [];
        }

        if (_programMembers is null) {
            _programMembers = new(StringComparer.Ordinal);
            void Gather(IEnumerable<Symbol> members) {
                foreach (var type in members.OfType<NamedTypeSymbol>()) {
                    Gather(type.Members);
                    if (type.TypeKind == TypeKind.Protocol) {
                        continue;
                    }

                    foreach (var member in type.Members) {
                        var made = member is FieldSymbol { IsComputed: true } property ? GetterOf(property) : member as FunctionSymbol;
                        if (made is not null) {
                            (_programMembers.TryGetValue(member.Name, out var named) ? named : _programMembers[member.Name] = []).Add(made);
                        }
                    }
                }
            }

            foreach (var module in _compilation.Modules.Where(static m => !CoreLibrary.IsStandardLibrary(m.Name))) {
                Gather(module.Members);
            }
        }

        name = member.ContainingSymbol is FieldSymbol field ? field.Name : name;
        return !_programMembers.TryGetValue(name, out var candidates) ? [] : candidates.Where(c =>
            c.Parameters.Length == parameters && Owner(c)?.OriginalDefinition is { } owner
            && (requirement is not null ? owner.Supertypes.Any(s => s is NamedTypeSymbol named && ReferenceEquals(named.OriginalDefinition, requirement.OriginalDefinition)) || owner.Satisfies(requirement)
                : !ReferenceEquals(owner, overridden) && owner.SupertypeOf(overridden!) is not null));
    }

    /// <summary>The globals some code names, and the functions it calls or names, in the order written.</summary>
    /// <param name="Maybe">The program's own members that may meet the protocol's properties read.</param>
    readonly record struct References(List<FieldSymbol> Globals, List<FunctionSymbol> Functions, List<FunctionSymbol> Maybe);

    /// <summary>What each function reads and calls, worked out once for every global that reaches it.</summary>
    readonly Dictionary<FunctionSymbol, References> _referencesForOrder = [];

    /// <summary>
    ///     What running a function reads and calls: what its body does, and
    ///     for an initialiser, what the initial values of its type's instance
    ///     fields do, as it stores them first.
    /// </summary>
    References ReferencesOf(FunctionSymbol function) {
        if (_referencesForOrder.TryGetValue(function, out var known)) {
            return known;
        }

        var bodies = new List<BoundNode>();
        var (syntax, model) = _declarations.TryGetValue(function, out var declaration)
            ? (declaration.Syntax, declaration.Model)
            : function.DeclaringSyntax is [var first, ..] && ModelOf(first) is { } found
                ? (first, found)
                : (null, null);

        if ((_compilation.GetDerivedBody(function) ?? _compilation.GetLazyBody(function) ?? _compilation.GetInheritedInitializerBody(function)
                ?? (syntax is null ? null : BodyModel(model!, syntax).GetBoundNode(syntax))) is { } body) {
            bodies.Add(body);
        }

        if (function.FunctionKind == FunctionKind.Initializer && Owner(function) is { } owner) {
            foreach (var instanceField in owner.OriginalDefinition.Members.OfType<FieldSymbol>().Where(static f => !f.IsStatic && !f.IsComputed)) {
                if (instanceField.DeclaringSyntax is [var fieldSyntax, ..] && ModelOf(fieldSyntax)?.GetFieldInitializer(instanceField) is { } initial) {
                    bodies.Add(initial);
                }
            }
        }

        var references = ReferencesOf(bodies);
        _referencesForOrder[function] = references;
        return references;
    }

    References ReferencesOf(IEnumerable<BoundNode> roots) {
        var globals = new List<FieldSymbol>();
        var functions = new List<FunctionSymbol>();
        var maybe = new List<FunctionSymbol>();

        void Function(FunctionSymbol? function) {
            if (function is not null) {
                functions.Add(function);
            }
        }

        foreach (var node in roots.SelectMany(Descendants)) {
            switch (node) {
                case BoundFieldAccess { Field: var field } when _globalsByField.ContainsKey(field):
                    globals.Add(field);
                    break;

                // A computed property's getter runs where it is read.
                case BoundFieldAccess { Field: { IsComputed: true } field }:
                    Function(GetterOf(field));
                    if (field.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol }) {
                        maybe.AddRange(MayRun(field, field.Name, 0));
                    }

                    break;

                case BoundCall call:
                    Function(call.Function);
                    break;

                case BoundFunctionReference reference:
                    Function(reference.Function);
                    break;

                case BoundBinaryOperator binary:
                    Function(binary.Method);
                    break;

                case BoundUnaryOperator unary:
                    Function(unary.Method);
                    break;

                case BoundIndexAccess { Subscript: { } subscript }:
                    Function(SubscriptGetter(subscript) ?? subscript);
                    break;
            }
        }

        return new(globals, functions, maybe);
    }

    /// <summary>
    ///     Reports globals whose initial values need each other, once, at the
    ///     first of them; <paramref name="cycle" /> is each global with the
    ///     functions it reads the next one through, the last reading the first.
    /// </summary>
    void ReportCycle(List<(FieldSymbol Global, IReadOnlyList<FunctionSymbol> Through)> cycle, HashSet<FieldSymbol> reported) {
        if (cycle.Count == 0 || cycle.Any(c => reported.Contains(c.Global))) {
            return;
        }

        foreach (var (global, _) in cycle) {
            reported.Add(global);
        }

        static string Name(FieldSymbol field) =>
            field.ContainingSymbol is NamedTypeSymbol type ? $"{type.Name}.{field.Name}" : field.Name;

        static string Reads(FieldSymbol reader, IReadOnlyList<FunctionSymbol> through, FieldSymbol read) =>
            through.Count == 0
                ? $"'{Name(reader)}' reads '{Name(read)}'"
                : $"'{Name(reader)}' reads '{Name(read)}' through {string.Join(", ", through.Select(static f => $"'{f.Name}()'"))}";

        static string Both(List<string> items) => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

        var steps = cycle.Select((step, i) => Reads(step.Global, step.Through, cycle[(i + 1) % cycle.Count].Global)).ToList();
        var first = cycle[0].Global;
        var message = cycle.Count == 1
            ? $"the initial value of '{Name(first)}' needs itself: {steps[0]}"
            : $"the initial values of {Both(cycle.Select(c => $"'{Name(c.Global)}'").ToList())} need each other, "
              + $"so none can be set first: {Both(steps)}";

        if (first.DeclaringSyntax is not [var syntax, ..] || ModelOf(syntax) is not { } model) {
            return;
        }

        var before = _diagnostics.Count;
        _diagnostics.Error(ErrorCode.ERR_GlobalInitializationCycle, IdentifierSpan(syntax, first) ?? syntax.Span, message);
        Place(before, model.SyntaxTree);
    }

    /// <summary>Where a global's name is written in its declaration.</summary>
    static TextSpan? IdentifierSpan(SyntaxNode declaration, FieldSymbol field) =>
        declaration.DescendantTokens().FirstOrDefault(t => t.Kind == SyntaxKind.IdentifierToken && t.Text == field.Name) is { IsNone: false } token
            ? token.Span
            : null;
}
