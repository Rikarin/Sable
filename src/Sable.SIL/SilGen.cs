using System.Collections.Immutable;
using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Lowers bound function bodies to SIL (docs/04 §2.2).
/// </summary>
/// <remarks>
///     <para>
///         What lowers so far: <c>Int</c>, <c>Bool</c>, <c>F64</c>,
///         <c>Char</c>; strings, with interpolation and the core's string
///         members; growable arrays; structs, with their methods,
///         <c>mutating</c> methods and initialisers; classes without
///         inheritance, with initialisers, methods and <c>deinit</c>; tuples;
///         unions, enums and optionals, with <c>match</c>, <c>if let</c>,
///         <c>if case</c>, <c>??</c> and <c>!</c>; <c>throws</c>, <c>try</c>,
///         <c>try?</c>, <c>try!</c>, <c>try … catch</c>, <c>do</c>/<c>catch</c>
///         and <c>throw</c>; <c>defer</c>; <c>if</c>, <c>guard</c>,
///         <c>while</c>, <c>repeat</c>, <c>for</c> over a range or an array,
///         labelled jumps. Anything else is reported as not lowered yet, by
///         construct, rather than lowered wrong.
///     </para>
///     <para>
///         Every local gets a stack slot: <c>alloc_stack</c> in the entry
///         block, <c>store</c> to write it, <c>load</c> to read it, and
///         <c>dealloc_stack</c> for each, in reverse, wherever the function
///         leaves. That is SIL's raw form, as Swift's SILGen produces it;
///         promotion to SSA values is a pass of its own.
///     </para>
///     <para>
///         Ownership is naive and explicit (docs/04 §2.1, docs/02 §2.2): an
///         expression's value is owned, +1; a parameter is borrowed, and copied
///         where something must own it; a temporary is destroyed after its
///         last use. A slot of a non-trivial type starts empty and is written
///         with <c>store [assign]</c>, which destroys what it held, so a loop
///         that declares a local releases the last iteration's value; every
///         such slot is destroyed where the function leaves. Owned temporaries
///         an expression holds while it evaluates the rest are kept on a stack,
///         so that a <c>throw</c> or <c>return</c> partway through destroys
///         them on its way out.
///     </para>
///     <para>
///         <c>defer</c> is lowered by emitting each deferred body on every
///         path that leaves its scope — falling off the end, <c>return</c>,
///         <c>throw</c>, <c>break</c>, <c>continue</c>, an error on its way to
///         a <c>catch</c> — innermost first. Errors go to a handler: the
///         nearest <c>do</c>'s catches, a <c>try?</c> or <c>try!</c> or
///         <c>try … catch</c>, or the function's own <c>throw</c>.
///     </para>
/// </remarks>
public sealed class SilGen {
    readonly Compilation _compilation;
    readonly DiagnosticBag _diagnostics = new();
    readonly SilModule _module = new();
    readonly Dictionary<FunctionSymbol, string> _names = [];
    readonly Dictionary<TypeSymbol, SilType?> _types = [];

    SilGen(Compilation compilation) {
        _compilation = compilation;
    }

    CoreTypes Core => _compilation.Core;

    public IReadOnlyList<Diagnostic> Diagnostics => [.._diagnostics];

    /// <summary>Lowers every function the compilation declares, or reports why it cannot.</summary>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(Compilation compilation) {
        var generator = new SilGen(compilation);
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    void LowerAll() {
        var functions = new List<(FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model)>();

        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.Root.DescendantNodes()) {
                if (declaration is not (FunctionDeclarationSyntax or InitDeclarationSyntax or DeinitDeclarationSyntax)) {
                    continue;
                }

                if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol symbol || !IsLowerable(symbol)) {
                    continue;
                }

                functions.Add((symbol, declaration, model));
            }
        }

        foreach (var (symbol, _, _) in functions) {
            var overloaded = functions.Count(f => f.Symbol.Name == symbol.Name
                && ReferenceEquals(f.Symbol.ContainingSymbol, symbol.ContainingSymbol)) > 1;
            _names[symbol] = Mangle(symbol, overloaded);
        }

        // '@main' marks where a program starts; failing that, a function
        // called main does.
        var entry = functions.FirstOrDefault(static f =>
                f.Syntax is FunctionDeclarationSyntax declaration
                && declaration.Attributes.Any(static a => a.Name.ToString().Trim() == "main")
            ).Symbol
            ?? functions.FirstOrDefault(static f => f.Symbol is { Name: "main", ContainingSymbol: ModuleSymbol }).Symbol;

        foreach (var (symbol, syntax, model) in functions) {
            _declarations[symbol] = (syntax, model);
        }

        // What is not generic is lowered as it is; what is, once for each set
        // of type arguments something calls it with.
        foreach (var (symbol, syntax, model) in functions) {
            if (IsGeneric(symbol)) {
                continue;
            }

            if (SignatureOf(symbol, syntax, TypeSubstitution.Empty) is not { } type) {
                continue;
            }

            if (ReferenceEquals(symbol, entry)) {
                _module.EntryPoint = _names[symbol];
            }

            LowerBody(symbol, syntax, model, _names[symbol], type, TypeSubstitution.Empty);
        }

        while (_pending.Count > 0) {
            var (symbol, name, type, substitution) = _pending.Dequeue();
            var (syntax, model) = _declarations[symbol];
            LowerBody(symbol, syntax, model, name, type, substitution);
        }
    }

    readonly Dictionary<FunctionSymbol, (SyntaxNode Syntax, SemanticModel Model)> _declarations = [];
    readonly Dictionary<string, FunctionType> _instances = new(StringComparer.Ordinal);
    readonly Queue<(FunctionSymbol Symbol, string Name, FunctionType Type, TypeSubstitution Substitution)> _pending = new();

    void LowerBody(FunctionSymbol symbol, SyntaxNode syntax, SemanticModel model, string name, FunctionType type, TypeSubstitution substitution) {
        if (model.GetBoundNode(syntax) is not BoundBlock body) {
            return;
        }

        var source = model.SyntaxTree.Text;
        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };

        if (HasSelf(symbol)) {
            function.ParameterNames.Add("self");
        }

        function.ParameterNames.AddRange(symbol.Parameters.Select(static p => p.Name));
        new FunctionLowering(this, symbol, function, source, substitution).Lower(body);
        function.RemoveUnreachableBlocks();
        _module.Add(function);

        if (symbol.FunctionKind == FunctionKind.Deinitializer && Lower(SelfType(symbol, substitution)) is ClassType classType) {
            classType.Deinit = function.Name;
        }
    }

    /// <summary>The type a member belongs to, whether declared in it or in an extension of it.</summary>
    internal static NamedTypeSymbol? OwnerOf(FunctionSymbol symbol) => Owner(symbol);

    static NamedTypeSymbol? Owner(FunctionSymbol symbol) =>
        symbol.ContainingSymbol switch {
            NamedTypeSymbol type => type,
            ExtensionSymbol extension => extension.ExtendedType,
            _ => null,
        };

    /// <summary>Every type parameter a function's body may mention: its type's, then its own.</summary>
    static IEnumerable<TypeParameterSymbol> TypeParametersOf(FunctionSymbol symbol) =>
        (Owner(symbol)?.OriginalDefinition.TypeParameters ?? []).Concat(symbol.TypeParameters);

    static bool IsGeneric(FunctionSymbol symbol) => TypeParametersOf(symbol).Any();

    /// <summary>
    ///     The instance of a generic function for these type arguments: its
    ///     name, and its signature — lowered later, once, however many calls
    ///     ask for it. Null when the arguments are not all known types.
    /// </summary>
    internal (string Name, FunctionType Type)? Instance(FunctionSymbol symbol, TypeSubstitution substitution, SyntaxNode at) {
        if (!_names.TryGetValue(symbol, out var baseName) || !_declarations.ContainsKey(symbol)) {
            return null;
        }

        var arguments = new List<SilType>();
        foreach (var parameter in TypeParametersOf(symbol)) {
            if (substitution.Lookup(parameter) is not { } argument || Lower(argument) is not { } lowered) {
                Unsupported<object>(at, $"'{symbol.Name}' with its type arguments unknown here");
                return null;
            }

            arguments.Add(lowered);
        }

        var name = $"{baseName}$of${string.Join("$", arguments.Select(static a => Sanitise(a.ToString())))}";
        if (_instances.TryGetValue(name, out var existing)) {
            return (name, existing);
        }

        if (SignatureOf(symbol, at, substitution) is not { } type) {
            return null;
        }

        _instances[name] = type;
        _pending.Enqueue((symbol, name, type, substitution));
        return (name, type);
    }

    /// <summary>
    ///     A function of the module, or a member of a struct, union, enum or
    ///     class, or of an extension of one. Local functions and classes that
    ///     take part in inheritance wait for closures and dynamic dispatch.
    /// </summary>
    static bool IsLowerable(FunctionSymbol symbol) =>
        symbol.ContainingSymbol is ModuleSymbol
        || Owner(symbol) switch {
            { TypeKind: TypeKind.Struct or TypeKind.Union or TypeKind.Enum } => true,
            { TypeKind: TypeKind.Class } type => IsFinalClass(type),
            _ => false,
        };

    /// <summary>A class that neither inherits nor can be inherited from: every call to its members is direct.</summary>
    static bool IsFinalClass(NamedTypeSymbol type) =>
        !type.IsOpen && !type.IsAbstract && !type.BaseTypes.Any(static b => b.TypeKind == TypeKind.Class);

    /// <summary>
    ///     <c>Module.name</c>, <c>Module.Type.name</c> for a member, and the
    ///     parameter types after it when the name is overloaded:
    ///     <c>M.area$Int$Int</c>.
    /// </summary>
    static string Mangle(FunctionSymbol symbol, bool overloaded) {
        var owner = Owner(symbol) is { } type
            ? $"{type.ContainingSymbol?.Name}.{type.Name}"
            : symbol.ContainingSymbol?.Name;

        var name = $"{owner}.{(symbol.FunctionKind == FunctionKind.Deinitializer ? "deinit" : symbol.Name)}";
        return overloaded
            ? name + string.Concat(symbol.Parameters.Select(static p => "$" + Sanitise(p.Type.ToDisplayString())))
            : name;
    }

    static string Sanitise(string text) =>
        new([..text.Select(static c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '$')]);

    /// <summary>True for a method that takes <c>self</c>, as opposed to a static one or an initialiser.</summary>
    static bool HasSelf(FunctionSymbol symbol) =>
        Owner(symbol) is not null && !symbol.IsStatic && symbol.FunctionKind != FunctionKind.Initializer;

    FunctionType? SignatureOf(FunctionSymbol symbol, SyntaxNode at, TypeSubstitution substitution) {
        SilType? Lower(TypeSymbol type) => this.Lower(substitution.Apply(type));

        if (symbol.IsAsync || symbol.IsRethrows) {
            return Unsupported<FunctionType>(at, $"'{symbol.Name}', which is async or rethrows");
        }

        var parameters = ImmutableArray.CreateBuilder<SilType>();

        // 'self' comes first: by value, or by address when the method may change it.
        if (HasSelf(symbol)) {
            if (this.Lower(SelfType(symbol, substitution)) is not { } self) {
                return Unsupported<FunctionType>(at, $"a member of '{SelfType(symbol, substitution).ToDisplayString()}'");
            }

            parameters.Add(symbol.IsMutating && self is not ClassType ? SilType.Address(self) : self);
        }

        foreach (var parameter in symbol.Parameters) {
            if (parameter.Modifier != ParameterModifier.None) {
                return Unsupported<FunctionType>(at, $"the '{parameter.Modifier}' parameter '{parameter.Name}'");
            }

            if (Lower(parameter.Type) is not { } type) {
                return Unsupported<FunctionType>(at, $"a parameter of type '{parameter.Type.ToDisplayString()}'");
            }

            parameters.Add(type);
        }

        var resultSymbol = symbol.FunctionKind == FunctionKind.Initializer ? SelfType(symbol, substitution) : symbol.ReturnType;
        if (Lower(resultSymbol) is not { } result) {
            return Unsupported<FunctionType>(at, $"a result of type '{resultSymbol.ToDisplayString()}'");
        }

        SilType? thrown = null;
        if (symbol.ThrownType is { } thrownSymbol) {
            if (substitution.Apply(thrownSymbol).Equals(Core.AnyError) || Lower(thrownSymbol) is not { } thrownType) {
                return Unsupported<FunctionType>(at, $"'{symbol.Name}', which throws '{thrownSymbol.ToDisplayString()}'");
            }

            thrown = thrownType;
        }

        return new FunctionType(parameters.ToImmutable(), result, thrown);
    }

    /// <summary>The type a member's <c>self</c> is: its owner, constructed with the type arguments in force.</summary>
    static TypeSymbol SelfType(FunctionSymbol symbol, TypeSubstitution substitution) {
        var owner = Owner(symbol)!.OriginalDefinition;
        return owner.TypeParameters.IsEmpty
            ? owner
            : owner.Construct([..owner.TypeParameters.Select(p => substitution.Apply(p))]);
    }

    // =======================================================================
    // Types
    // =======================================================================

    /// <summary>The language types lowered so far, in order, so that a failure can take back what it started.</summary>
    readonly List<TypeSymbol> _lowered = [];

    /// <summary>The SIL type for a language type, or null when there is none yet.</summary>
    /// <remarks>
    ///     A named type is entered before its members are lowered, so that a
    ///     member reaching it again through a reference — a class's own type,
    ///     an array of it — finds it. One that holds itself by value has no
    ///     size and no SIL type; nor has one with a member that has none, and
    ///     every type begun while lowering it is taken back with it.
    /// </remarks>
    internal SilType? Lower(TypeSymbol type) {
        if (_types.TryGetValue(type, out var cached)) {
            return cached;
        }

        var lowered = _lowered.Count;
        var types = _module.Types.Count;
        var result = LowerUncached(type);
        if (result is null) {
            // Take back every type begun inside this one.
            foreach (var begun in _lowered.Skip(lowered)) {
                _types.Remove(begun);
            }

            _lowered.RemoveRange(lowered, _lowered.Count - lowered);
            foreach (var added in _module.Types.Skip(types).ToList()) {
                _module.RemoveType(added);
            }
        }

        _types[type] = result;
        _lowered.Add(type);
        return result;
    }

    /// <summary>Enters a named type before its members, so that they may refer to it.</summary>
    T Begin<T>(TypeSymbol symbol, T type) where T : NominalType {
        _types[symbol] = type;
        _lowered.Add(symbol);
        return _module.AddType(type);
    }

    SilType? LowerUncached(TypeSymbol type) {
        if (type.Equals(Core.Int)) {
            return SilType.Int;
        }

        if (type.Equals(Core.Bool)) {
            return SilType.Bool;
        }

        if (type.Equals(Core.F64)) {
            return SilType.F64;
        }

        if (type.Equals(Core.Char)) {
            return SilType.Char;
        }

        if (type.Equals(Core.Void)) {
            return SilType.Unit;
        }

        if (type.Equals(Core.String)) {
            return SilType.String;
        }

        switch (type) {
            case TupleTypeSymbol tuple: {
                var elements = ImmutableArray.CreateBuilder<SilType>();
                foreach (var element in tuple.ElementTypes) {
                    if (Lower(element) is not { } lowered) {
                        return null;
                    }

                    elements.Add(lowered);
                }

                return SilType.Tuple(elements.ToImmutable());
            }

            // The growable kinds, and the fixed one, share a counted buffer.
            case ArrayTypeSymbol { ArrayKind: ArrayKind.Grow or ArrayKind.GrowShrink or ArrayKind.Fixed } array:
                return Lower(array.ElementType) is { } elementType ? new ArrayType(elementType) : null;

            // A function as a value: code and a context, called with both.
            case FunctionTypeSymbol { IsAsync: false, IsRethrows: false } functionType: {
                var parameters = ImmutableArray.CreateBuilder<SilType>();
                foreach (var parameter in functionType.ParameterTypes) {
                    if (Lower(parameter) is not { } lowered) {
                        return null;
                    }

                    parameters.Add(lowered);
                }

                if (Lower(functionType.ReturnType) is not { } result) {
                    return null;
                }

                SilType? thrown = null;
                if (functionType.ThrownType is { } thrownSymbol && (thrownSymbol.Equals(Core.AnyError) || (thrown = Lower(thrownSymbol)) is null)) {
                    return null;
                }

                return new ThickFunctionType(new FunctionType(parameters.ToImmutable(), result, thrown));
            }

            case OptionalTypeSymbol optional: {
                if (Lower(optional.UnderlyingType) is not { } wrapped) {
                    return null;
                }

                var name = $"Optional${Sanitise(wrapped.ToString())}";
                if (_module.FindType(name) is EnumType existing) {
                    return existing;
                }

                var enumType = Begin(type, new EnumType(name));
                enumType.Cases.Add(("Some", wrapped));
                enumType.Cases.Add(("None", null));
                return HoldsItself(enumType) ? null : enumType;
            }

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Struct } named: {
                var structType = Begin(named, new StructType(TypeName(named)));
                var members = TypeSubstitution.For(named);
                foreach (var field in StoredFields(named.OriginalDefinition)) {
                    if (Lower(members.Apply(field.Type)) is not { } fieldType) {
                        return null;
                    }

                    structType.Fields.Add((field.Name, fieldType));
                }

                return HoldsItself(structType) ? null : structType;
            }

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Class } named when IsFinalClass(named.OriginalDefinition): {
                var classType = Begin(named, new ClassType(TypeName(named)));
                var members = TypeSubstitution.For(named);
                foreach (var field in StoredFields(named.OriginalDefinition)) {
                    if (Lower(members.Apply(field.Type)) is not { } fieldType) {
                        return null;
                    }

                    classType.Fields.Add((field.Name, fieldType));
                }

                return classType;
            }

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Union } named: {
                var enumType = Begin(named, new EnumType(TypeName(named)));
                var members = TypeSubstitution.For(named);
                foreach (var unionCase in named.OriginalDefinition.Members.OfType<UnionCaseSymbol>()) {
                    SilType? payload = null;
                    if (unionCase.Payload.Length > 0) {
                        var parts = ImmutableArray.CreateBuilder<SilType>();
                        foreach (var field in unionCase.Payload) {
                            if (Lower(members.Apply(field.Type)) is not { } part) {
                                return null;
                            }

                            parts.Add(part);
                        }

                        payload = parts.Count == 1 ? parts[0] : SilType.Tuple(parts.ToImmutable());
                    }

                    enumType.Cases.Add((unionCase.Name, payload));
                }

                return HoldsItself(enumType) ? null : enumType;
            }

            case NamedTypeSymbol { TypeKind: TypeKind.Enum } named: {
                var enumType = Begin(named, new EnumType(TypeName(named)));
                foreach (var enumCase in named.Members.OfType<EnumCaseSymbol>()) {
                    enumType.Cases.Add((enumCase.Name, null));
                }

                return enumType;
            }

            default:
                return null;
        }
    }

    /// <summary>
    ///     The box a local lives in when a closure captures it by reference:
    ///     an object of one field, shared by the frame and every closure.
    /// </summary>
    internal ClassType BoxType(SilType type) {
        var name = $"Box$of${Sanitise(type.ToString())}";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        var box = _module.AddType(new ClassType(name));
        box.Fields.Add(("value", type));
        return box;
    }

    readonly Dictionary<string, int> _closureCounts = new(StringComparer.Ordinal);

    internal string ClosureName(string function) {
        var count = _closureCounts.GetValueOrDefault(function);
        _closureCounts[function] = count + 1;
        return $"{function}$closure{count}";
    }

    /// <summary>One thing a closure captured: where in its context it is, and whether that is a box.</summary>
    internal sealed record Capture(object Key, TypeSymbol Type, int Field, bool Boxed, BoundExpression? Initializer);

    /// <summary>The key a captured <c>self</c> is filed under.</summary>
    internal const string SelfKey = "self";

    static IEnumerable<BoundNode> Descendants(BoundNode node) {
        var pending = new Stack<BoundNode>();
        pending.Push(node);
        while (pending.Count > 0) {
            var next = pending.Pop();
            yield return next;
            foreach (var child in next.Children.Reverse()) {
                pending.Push(child);
            }
        }
    }

    /// <summary>
    ///     What a closure captures, in order of first use: the locals of the
    ///     functions around it by reference, through boxes; their parameters,
    ///     <c>self</c>, and what its capture list names, by value.
    /// </summary>
    internal static List<Capture> CapturesOf(BoundClosure closure) {
        var inner = Descendants(closure.Body).OfType<BoundClosure>().Select(static c => (Symbol)c.Symbol).ToHashSet();
        inner.Add(closure.Symbol);

        var listed = closure.Captures.Where(static c => c.Initializer is not null).ToDictionary(static c => c.Symbol, static c => c);
        var captures = new List<Capture>();
        var seen = new HashSet<object>();

        void Add(object key, TypeSymbol type, bool boxed, BoundExpression? initializer) {
            if (seen.Add(key)) {
                captures.Add(new(key, type, captures.Count, boxed, initializer));
            }
        }

        foreach (var node in Descendants(closure.Body)) {
            switch (node) {
                case BoundLocalAccess access when !inner.Contains(access.Local.ContainingSymbol!):
                    if (listed.TryGetValue(access.Local, out var listedCapture)) {
                        Add(access.Local, access.Local.Type, false, listedCapture.Initializer);
                    } else {
                        Add(access.Local, access.Local.Type, true, null);
                    }

                    break;

                case BoundParameterAccess access when !inner.Contains(access.Parameter.ContainingSymbol!):
                    Add(access.Parameter, access.Parameter.Type, false, null);
                    break;

                case BoundSelfAccess self:
                    Add(SelfKey, self.Type, false, null);
                    break;
            }
        }

        return captures;
    }

    /// <summary>The locals of a function that some closure inside it captures by reference: they live in boxes.</summary>
    internal static HashSet<LocalSymbol> BoxedLocals(Symbol function, BoundNode body) {
        var boxed = new HashSet<LocalSymbol>();
        foreach (var closure in Descendants(body).OfType<BoundClosure>()) {
            foreach (var capture in CapturesOf(closure)) {
                if (capture is { Boxed: true, Key: LocalSymbol local } && ReferenceEquals(local.ContainingSymbol, function)) {
                    boxed.Add(local);
                }
            }
        }

        return boxed;
    }

    /// <summary>
    ///     A named function as a closure's code: a thunk that takes the
    ///     context it does not need, and calls the function.
    /// </summary>
    internal string Thunk(string name, FunctionType type) {
        var thunkName = name + "$thunk";
        if (_module.Find(thunkName) is not null) {
            return thunkName;
        }

        var thunk = new SilFunction(thunkName, new ThickFunctionType(type).Implementation);
        var entry = thunk.AddBlock();
        var arguments = type.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToArray();
        entry.AddArgument(SilType.Object);
        var reference = entry.Append(new FunctionRefInst(name, type));
        if (type.Thrown is null) {
            var result = entry.Append(new ApplyInst(reference, type.Result, arguments));
            entry.Append(new ReturnInst(type.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(type.Thrown);
            entry.Append(new TryApplyInst(reference, type.Result, arguments, normal, failed));
            normal.Append(new ReturnInst(result));
            failed.Append(new ThrowInst(error));
        }

        _module.Add(thunk);
        return thunkName;
    }

    /// <summary>True when a type contains itself by value — through no reference — and so has no size.</summary>
    static bool HoldsItself(NominalType type) {
        var seen = new HashSet<SilType>();
        bool Reaches(SilType part) {
            if (ReferenceEquals(part, type)) {
                return true;
            }

            if (!seen.Add(part)) {
                return false;
            }

            return part switch {
                StructType structType => structType.Fields.Any(f => Reaches(f.Type)),
                TupleType tuple => tuple.Elements.Any(Reaches),
                EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && Reaches(payload)),
                _ => false, // a scalar, or something reached through a reference
            };
        }

        return type switch {
            StructType structType => structType.Fields.Any(f => Reaches(f.Type)),
            EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && Reaches(payload)),
            _ => false,
        };
    }

    /// <summary><c>M.Point</c>; a constructed type adds its arguments: <c>M.Box$of$Int</c>.</summary>
    string TypeName(NamedTypeSymbol type) {
        var name = $"{type.OriginalDefinition.ContainingSymbol?.Name}.{type.Name}";
        if (!type.IsConstructed) {
            return name;
        }

        var arguments = type.TypeArguments.Select(a => Lower(a)?.ToString() ?? a.ToDisplayString());
        return $"{name}$of${string.Join("$", arguments.Select(Sanitise))}";
    }

    internal static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false });

    T? Unsupported<T>(SyntaxNode at, string what) where T : class {
        _diagnostics.Error(ErrorCode.ERR_NotLoweredYet, at.Span, $"SIL lowering does not handle {what} yet");
        return null;
    }

    // =======================================================================

    /// <summary>What a closure's code knows of its context: its class, and where each capture is in it.</summary>
    sealed record ClosureInfo(ClassType? Context, Dictionary<object, Capture> Captures);

    sealed class FunctionLowering(
        SilGen generator,
        FunctionSymbol symbol,
        SilFunction function,
        SourceText source,
        TypeSubstitution substitution,
        ClosureInfo? closure = null
    ) {
        /// <summary>In a closure's code, its context, as the class it is.</summary>
        SilValue? _context;

        /// <summary>This function's locals that closures capture by reference, each kept in a box.</summary>
        HashSet<LocalSymbol> _boxed = [];

        /// <summary>A type as this instance sees it: its type parameters replaced by what they stand for.</summary>
        /// <remarks>
        ///     Inside a generic type, <c>self</c> has the type's own definition
        ///     as its type, which names no arguments; this instance supplies them.
        /// </remarks>
        TypeSymbol Sub(TypeSymbol type) =>
            type is NamedTypeSymbol { IsGenericDefinition: true } definition
            && definition.TypeParameters.All(p => substitution.Lookup(p) is not null)
                ? definition.Construct([..definition.TypeParameters.Select(p => substitution.Lookup(p)!)])
                : substitution.Apply(type);

        SilType? LowerType(TypeSymbol type) => generator.Lower(Sub(type));

        /// <summary>Marks an expression whose value is <c>()</c>: something to have done, nothing to use.</summary>
        sealed class UnitValue : SilValue {
            public static readonly UnitValue Instance = new();
            public override SilType Type => SilType.Unit;
        }

        sealed record Loop(string? Label, SilBlock Break, SilBlock Continue, int Scopes, int Temporaries);

        /// <summary>Where an error goes: a block taking it, and how many defer scopes and temporaries were open there.</summary>
        sealed record Handler(SilBlock Block, SilType Type, int Scopes, int Temporaries);

        readonly Dictionary<ParameterSymbol, SilValue> _parameters = [];
        readonly Dictionary<LocalSymbol, AllocStackInst> _slots = [];
        readonly List<AllocStackInst> _allocations = [];
        readonly List<Loop> _loops = [];
        readonly List<Handler> _handlers = [];

        /// <summary>The deferred bodies of each open scope, innermost last.</summary>
        readonly List<List<BoundStatement>> _scopes = [];

        /// <summary>
        ///     Owned values an expression holds while it evaluates the rest of
        ///     itself: what a <c>throw</c> or <c>return</c> from inside it must
        ///     destroy on the way out.
        /// </summary>
        readonly List<SilValue> _temporaries = [];

        SilValue? _selfValue;
        SilValue? _selfAddress;

        /// <summary>In a class's initialiser, the slot holding the object being built.</summary>
        AllocStackInst? _selfObject;

        SilBlock? _current;

        /// <summary>The source position instructions emitted now belong to.</summary>
        SilLocation? _location;

        void Locate(SyntaxNode syntax) {
            var position = source.GetLinePosition(syntax.Span.Start);
            _location = new SilLocation(position.Line, position.Column);
        }

        SilBlock _entry = null!;
        bool _failed;

        CoreTypes Core => generator.Core;

        public void Lower(BoundBlock body) {
            _entry = function.AddBlock();
            var parameterTypes = function.Type.Parameters;
            var next = 0;

            if (HasSelf(symbol)) {
                var self = _entry.AddArgument(parameterTypes[next++]);
                if (self.Type is AddressType) {
                    _selfAddress = self;
                } else {
                    _selfValue = self;
                }
            }

            foreach (var parameter in symbol.Parameters) {
                _parameters[parameter] = _entry.AddArgument(parameterTypes[next++]);
            }

            _current = _entry;
            _boxed = BoxedLocals(symbol, body);

            // A closure's code takes its context last.
            if (closure is not null) {
                var context = _entry.AddArgument(SilType.Object);
                if (closure.Context is { } contextType) {
                    _context = Emit(new UncheckedRefCastInst(context, contextType));
                }
            }

            // An initialiser builds 'self' in a slot of its own: a struct field
            // by field, a class as an object whose fields are then written.
            if (symbol.FunctionKind == FunctionKind.Initializer) {
                if (function.Type.Result is ClassType classType) {
                    _selfObject = NewSlot(classType, "self");
                    Emit(new StoreInst(Emit(new AllocRefInst(classType)), _selfObject, StoreKind.Assign));
                } else {
                    _selfAddress = NewSlot(function.Type.Result, "self");
                }
            }

            Statement(body);

            // Falling off the end: fine for a function returning nothing, and
            // for an initialiser, which returns what it built. For one with a
            // result the binder has said every path returns, so the end
            // cannot be reached.
            if (_current is not null) {
                if (symbol.FunctionKind == FunctionKind.Initializer) {
                    Return(TakeSelf());
                } else if (function.Type.Result == SilType.Unit) {
                    Return(null);
                } else {
                    Jump(new UnreachableInst());
                }
            }

            // The exits, built last, so that they free every slot the body made.
            if (_returnBlock is { } returnBlock) {
                _current = returnBlock;
                _location = null;
                Deallocate();
                Jump(new ReturnInst(returnBlock.Arguments.Count == 0 ? null : returnBlock.Arguments[0]));
            }

            if (_throwBlock is { } throwBlock) {
                _current = throwBlock;
                Deallocate();
                Jump(new ThrowInst(throwBlock.Arguments[0]));
            }

            if (_failed) {
                // Leave a function that verifies, so the rest still can.
                function.Blocks.Clear();
                var stub = function.AddBlock();
                foreach (var parameter in function.Type.Parameters) {
                    stub.AddArgument(parameter);
                }

                stub.Append(new UnreachableInst());
            }
        }

        /// <summary>What an initialiser returns: the value or object it built, taken out of its slot.</summary>
        SilValue TakeSelf() {
            var slot = _selfObject ?? _selfAddress!;
            var type = ((AddressType)slot.Type).Pointee;
            return Emit(new LoadInst(slot, type.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
        }

        SilBlock? _throwBlock;

        // -------------------------------------------------------------------
        // Emitting
        // -------------------------------------------------------------------

        void Fail(SyntaxNode at, string what) {
            if (!_failed) {
                generator.Unsupported<object>(at, what);
            }

            _failed = true;
        }

        T Emit<T>(T instruction) where T : SilInstruction {
            // After a jump nothing is reachable until a label; code there is
            // lowered into a block of its own, which is then dropped.
            _current ??= function.AddBlock();
            instruction.Location ??= _location;
            return _current.Append(instruction);
        }

        void Jump(SilInstruction terminator) {
            Emit(terminator);
            _current = null;
        }

        void Branch(SilBlock target, params SilValue[] arguments) => Jump(new BranchInst(target, arguments));

        /// <summary>A stack slot, allocated in the entry block so that one place frees them all.</summary>
        AllocStackInst NewSlot(SilType type, string? variable = null) {
            var slot = new AllocStackInst(type) { Block = _entry, VariableName = variable, Location = _location };
            _entry.Instructions.Insert(_allocations.Count, slot);
            _allocations.Add(slot);
            return slot;
        }

        AllocStackInst? SlotFor(LocalSymbol local, SyntaxNode at) {
            if (_slots.TryGetValue(local, out var slot)) {
                return slot;
            }

            if (LowerType(local.Type) is not { } type) {
                Fail(at, $"a local of type '{local.Type.ToDisplayString()}'");
                return null;
            }

            return _slots[local] = NewSlot(_boxed.Contains(local) ? generator.BoxType(type) : type, local.Name);
        }

        /// <summary>
        ///     Gives a local a value where it is bound: a declaration, a
        ///     pattern, a loop's element. A local closures share gets a fresh
        ///     box each time, so that each closure sees the binding it was made in.
        /// </summary>
        void BindLocal(LocalSymbol local, SilValue? value, SyntaxNode at, bool declaration = false) {
            if (SlotFor(local, at) is not { } slot) {
                return;
            }

            if (_boxed.Contains(local)) {
                var box = Emit(new AllocRefInst((ClassType)slot.ElementType));
                if (value is not null) {
                    Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(box, 0)), StoreKind.Assign));
                }

                Emit(new StoreInst(box, slot, StoreKind.Assign));
                return;
            }

            if (value is not null) {
                Store(value, slot, declaration);
            }
        }

        /// <summary>
        ///     Where a local's value is: its slot; its box, reached through the
        ///     slot or, in a closure, through the context; or, for a value a
        ///     closure captured, its place in the context. A box is borrowed
        ///     until the caller ends <paramref name="borrows" />.
        /// </summary>
        SilValue? LocalAddress(LocalSymbol local, SyntaxNode at, List<SilValue> borrows) {
            if (closure is not null && closure.Captures.TryGetValue(local, out var capture)) {
                var field = Emit(new RefElementAddrInst(_context!, capture.Field));
                if (!capture.Boxed) {
                    return field;
                }

                var capturedBox = Hold(Emit(new LoadInst(field)));
                borrows.Add(capturedBox);
                return Emit(new RefElementAddrInst(capturedBox, 0));
            }

            if (SlotFor(local, at) is not { } slot) {
                return null;
            }

            if (!_boxed.Contains(local)) {
                return slot;
            }

            var box = Hold(Emit(new LoadInst(slot)));
            borrows.Add(box);
            return Emit(new RefElementAddrInst(box, 0));
        }

        SilValue? LoadLocal(BoundLocalAccess access) {
            var borrows = new List<SilValue>();
            if (LocalAddress(access.Local, access.Syntax, borrows) is not { } address) {
                return null;
            }

            var value = Emit(new LoadInst(address));
            EndAll(borrows);
            return value;
        }

        /// <summary>Destroys what every non-trivial slot holds, then frees the slots, in reverse.</summary>
        void Deallocate() {
            for (var i = _allocations.Count - 1; i >= 0; i--) {
                if (!_allocations[i].ElementType.IsTrivial) {
                    Emit(new DestroyAddrInst(_allocations[i]));
                }
            }

            for (var i = _allocations.Count - 1; i >= 0; i--) {
                Emit(new DeallocStackInst(_allocations[i]));
            }
        }

        // -------------------------------------------------------------------
        // Ownership
        // -------------------------------------------------------------------

        static bool IsOwned(SilValue value) =>
            value is not UnitValue && SilVerifier.OwnershipOf(value) == Ownership.Owned;

        /// <summary>A value something can take ownership of: itself if owned or trivial, else a copy.</summary>
        SilValue Owned(SilValue value) =>
            value is UnitValue || SilVerifier.OwnershipOf(value) != Ownership.Guaranteed
                ? value
                : Emit(new CopyValueInst(value));

        /// <summary>The end of an owned value's life; nothing for a borrowed or trivial one.</summary>
        void Destroy(SilValue value) {
            if (IsOwned(value)) {
                Emit(new DestroyValueInst(value));
            }
        }

        /// <summary>Keeps an owned value on the temporaries while more of the expression is evaluated.</summary>
        SilValue Hold(SilValue value) {
            if (IsOwned(value)) {
                _temporaries.Add(value);
            }

            return value;
        }

        /// <summary>Takes a held value back off the temporaries, for whatever consumes it next.</summary>
        SilValue Unhold(SilValue value) {
            if (IsOwned(value)) {
                _temporaries.RemoveAt(_temporaries.LastIndexOf(value));
            }

            return value;
        }

        /// <summary>The end of a borrow: a temporary is taken off the temporaries and destroyed.</summary>
        void End(SilValue value) => Destroy(Unhold(value));

        /// <summary>Destroys, on the path being emitted, the temporaries above a depth, without forgetting them.</summary>
        void DestroyTemporaries(int depth) {
            for (var i = _temporaries.Count - 1; i >= depth; i--) {
                Emit(new DestroyValueInst(_temporaries[i]));
            }
        }

        /// <summary>
        ///     An expression's value to use without taking it: a parameter or
        ///     <c>self</c>, or a field of one, as it is; anything else evaluated
        ///     and held as a temporary, to be <see cref="End" />ed after the use.
        /// </summary>
        SilValue? Borrow(BoundExpression expression) {
            switch (expression) {
                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var parameter):
                    return parameter;

                case BoundSelfAccess when _selfValue is not null:
                    return _selfValue;

                case BoundConversion { ConversionKind: ConversionKind.Identity } conversion:
                    return Borrow(conversion.Operand);

                // A stored field of something borrowed is itself borrowed.
                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when Sub(receiver.Type).TypeKind == TypeKind.Struct && IsBorrowable(receiver): {
                    if (Borrow(receiver) is not { Type: StructType structType } value) {
                        return null;
                    }

                    var index = structType.FieldIndex(field.Name);
                    return index < 0 ? Missing(expression, $"the member '{field.Name}'") : Emit(new StructExtractInst(value, index));
                }

                default:
                    return Expression(expression) is { } owned ? Hold(owned) : null;
            }
        }

        /// <summary>True for an expression <see cref="Borrow" /> reaches without evaluating anything owned.</summary>
        bool IsBorrowable(BoundExpression expression) =>
            expression switch {
                BoundParameterAccess access => _parameters.ContainsKey(access.Parameter),
                BoundSelfAccess => _selfValue is not null,
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => IsBorrowable(conversion.Operand),
                BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } }
                    => Sub(receiver.Type).TypeKind == TypeKind.Struct && IsBorrowable(receiver),
                _ => false,
            };

        /// <summary>Evaluates expressions in order, holding each while the next is evaluated.</summary>
        List<SilValue>? Evaluate(IEnumerable<BoundExpression> expressions) {
            var values = new List<SilValue>();
            foreach (var expression in expressions) {
                if (Expression(expression) is not { } value) {
                    values.ForEach(v => Unhold(v));
                    return null;
                }

                values.Add(Hold(value));
            }

            // Whatever consumes them next owns them.
            for (var i = values.Count - 1; i >= 0; i--) {
                Unhold(values[i]);
            }

            return values;
        }

        /// <summary>A payload argument for a <c>switch_enum</c> case block: borrowed from the enum.</summary>
        static SilArgument Payload(SilBlock block, SilType type) => block.AddArgument(type, !type.IsTrivial);

        /// <summary>A block that destroys a value and goes on to another; or the other itself, when there is nothing to destroy.</summary>
        SilBlock Destroying(SilValue value, SilBlock target) {
            if (!IsOwned(value)) {
                return target;
            }

            var saved = _current;
            var block = function.AddBlock();
            _current = block;
            Destroy(value);
            Branch(target);
            _current = saved;
            return block;
        }

        /// <summary>Runs the deferred bodies of every scope deeper than <paramref name="depth" />, innermost first.</summary>
        void RunDefers(int depth) {
            var saved = _scopes.ToList();
            for (var i = saved.Count - 1; i >= depth; i--) {
                // While a scope's defers run, it and those inside it are closed.
                _scopes.RemoveRange(i, _scopes.Count - i);
                foreach (var deferred in Enumerable.Reverse(saved[i])) {
                    Statement(deferred);
                }
            }

            _scopes.Clear();
            _scopes.AddRange(saved);
        }

        /// <summary>
        ///     The one block every return goes through. Its deallocations are
        ///     written last, when every slot is known: a slot made after some
        ///     return was lowered must still be freed on that return's path.
        /// </summary>
        SilBlock? _returnBlock;

        void Return(SilValue? value) {
            // A return from inside an expression leaves its temporaries behind.
            DestroyTemporaries(0);
            RunDefers(0);

            if (_returnBlock is null) {
                _returnBlock = function.AddBlock();
                if (function.Type.Result != SilType.Unit) {
                    _returnBlock.AddArgument(function.Type.Result);
                }
            }

            if (function.Type.Result == SilType.Unit) {
                Branch(_returnBlock);
            } else if (value is not null and not UnitValue) {
                Branch(_returnBlock, Owned(value));
            }
        }

        /// <summary>Sends an error to the innermost handler, running the defers on the way.</summary>
        void RouteError(SilValue error, SyntaxNode at) {
            var handler = _handlers.Count > 0 ? _handlers[^1] : FunctionHandler(error.Type, at);
            if (handler is null) {
                return;
            }

            if (handler.Type != error.Type) {
                Fail(at, $"an error of type {error.Type} converted to {handler.Type}");
                return;
            }

            DestroyTemporaries(handler.Temporaries);
            RunDefers(handler.Scopes);
            Branch(handler.Block, Owned(error));
        }

        /// <summary>The function's own throw, as a handler.</summary>
        Handler? FunctionHandler(SilType errorType, SyntaxNode at) {
            if (function.Type.Thrown is not { } thrown) {
                Fail(at, "an error where nothing can handle it");
                return null;
            }

            if (_throwBlock is null) {
                _throwBlock = function.AddBlock();
                _throwBlock.AddArgument(thrown);
            }

            _ = errorType;
            return new(_throwBlock, thrown, 0, 0);
        }

        // -------------------------------------------------------------------
        // Statements
        // -------------------------------------------------------------------

        void Statement(BoundStatement statement) {
            if (_failed) {
                return;
            }

            if (statement is not BoundBlock) {
                Locate(statement.Syntax);
            }

            switch (statement) {
                case BoundBlock block:
                    _scopes.Add([]);
                    foreach (var child in block.Statements) {
                        Statement(child);
                    }

                    if (_current is not null) {
                        RunDefers(_scopes.Count - 1);
                    }

                    _scopes.RemoveAt(_scopes.Count - 1);
                    return;

                case BoundDefer defer:
                    _scopes[^1].Add(defer.Body);
                    return;

                case BoundLocalDeclaration declaration:
                    if (declaration.Initializer is { } initializer) {
                        if (Expression(initializer) is { } initial) {
                            BindLocal(declaration.Local, initial, declaration.Syntax, declaration: true);
                        }
                    } else if (_boxed.Contains(declaration.Local)) {
                        BindLocal(declaration.Local, null, declaration.Syntax);
                    }

                    return;

                case BoundPatternDeclaration declaration: {
                    if (Expression(declaration.Initializer) is not { } value) {
                        return;
                    }

                    // Irrefutable, so the failing branch cannot be taken.
                    var matched = function.AddBlock();
                    var failed = function.AddBlock();
                    Hold(value);
                    Pattern(declaration.Pattern, value, matched, failed);
                    Unhold(value);
                    _current = failed;
                    Jump(new UnreachableInst());
                    _current = matched;
                    Destroy(value);
                    return;
                }

                case BoundExpressionStatement expression:
                    if (Expression(expression.Expression) is { } discarded) {
                        Destroy(discarded);
                    }

                    return;

                case BoundReturn returnStatement: {
                    if (returnStatement.Value is { } returned) {
                        if (Expression(returned) is { } value) {
                            Return(value);
                        }
                    } else if (symbol.FunctionKind == FunctionKind.Initializer) {
                        Return(TakeSelf());
                    } else {
                        Return(null);
                    }

                    return;
                }

                case BoundThrow throwStatement:
                    if (Expression(throwStatement.Value) is { } error) {
                        RouteError(error, throwStatement.Syntax);
                    }

                    return;

                case BoundIf ifStatement:
                    If(ifStatement);
                    return;

                case BoundWhile loop: {
                    var header = function.AddBlock();
                    var body = function.AddBlock();
                    var after = function.AddBlock();
                    Branch(header);

                    _current = header;
                    Condition(loop.Condition, body, after);

                    _current = body;
                    InLoop(loop, after, header, () => Statement(loop.Body));
                    if (_current is not null) {
                        Branch(header);
                    }

                    _current = after;
                    return;
                }

                case BoundRepeat loop: {
                    var body = function.AddBlock();
                    var test = function.AddBlock();
                    var after = function.AddBlock();
                    Branch(body);

                    _current = body;
                    InLoop(loop, after, test, () => Statement(loop.Body));
                    if (_current is not null) {
                        Branch(test);
                    }

                    _current = test;
                    Condition(loop.Condition, body, after);

                    _current = after;
                    return;
                }

                case BoundForEach { Sequence: BoundRange range } loop:
                    ForRange(loop, range);
                    return;

                case BoundForEach { Sequence.Type: ArrayTypeSymbol } loop:
                    ForArray(loop);
                    return;

                case BoundBreak jump when FindLoop(jump.Label) is { } breakLoop:
                    DestroyTemporaries(breakLoop.Temporaries);
                    RunDefers(breakLoop.Scopes);
                    Branch(breakLoop.Break);
                    return;

                case BoundContinue jump when FindLoop(jump.Label) is { } continueLoop:
                    DestroyTemporaries(continueLoop.Temporaries);
                    RunDefers(continueLoop.Scopes);
                    Branch(continueLoop.Continue);
                    return;

                case BoundDo doStatement:
                    Do(doStatement);
                    return;

                default:
                    Fail(statement.Syntax, $"the statement '{Describe(statement.Syntax)}'");
                    return;
            }
        }

        /// <summary>
        ///     Stores an owned value: <c>[init]</c> for a trivial declaration, as
        ///     its slot is fresh; <c>[assign]</c> otherwise, which destroys any
        ///     value the storage held — the last iteration's, in a loop.
        /// </summary>
        void Store(SilValue value, SilValue address, bool declaration = false) =>
            Emit(new StoreInst(Owned(value), address, declaration && value.Type.IsTrivial ? StoreKind.Init : StoreKind.Assign));

        void If(BoundIf ifStatement) {
            var after = function.AddBlock();

            // 'guard c else { body }': the body runs when c is false.
            if (ifStatement.Syntax is GuardStatementSyntax) {
                var elseBody = function.AddBlock();
                Condition(ifStatement.Condition, after, elseBody);
                _current = elseBody;
                Statement(ifStatement.Consequence);
                if (_current is not null) {
                    Branch(after);
                }

                _current = after;
                return;
            }

            var consequence = function.AddBlock();
            var alternative = ifStatement.Alternative is null ? after : function.AddBlock();
            Condition(ifStatement.Condition, consequence, alternative);

            _current = consequence;
            Statement(ifStatement.Consequence);
            if (_current is not null) {
                Branch(after);
            }

            if (ifStatement.Alternative is { } otherwise) {
                _current = alternative;
                Statement(otherwise);
                if (_current is not null) {
                    Branch(after);
                }
            }

            _current = after;
        }

        /// <summary><c>for i in a..&lt;b</c> and <c>a...b</c>: a counting loop, with no range built.</summary>
        void ForRange(BoundForEach loop, BoundRange range) {
            if (range.Lower is null || range.Upper is null || loop.Element is null) {
                Fail(loop.Syntax, "a 'for' over an open range");
                return;
            }

            if (Expression(range.Lower) is not { } lower || Expression(range.Upper) is not { } upper) {
                return;
            }

            if (lower.Type != SilType.Int || SlotFor(loop.Element, loop.Syntax) is null) {
                Fail(loop.Syntax, "a 'for' over a range that is not of Int");
                return;
            }

            var counter = NewSlot(SilType.Int);
            Emit(new StoreInst(lower, counter, StoreKind.Init));

            var header = function.AddBlock();
            var body = function.AddBlock();
            var step = function.AddBlock();
            var after = function.AddBlock();
            Branch(header);

            _current = header;
            var current = Emit(new LoadInst(counter));
            var compare = range.IsInclusive ? "cmp_le_Int" : "cmp_lt_Int";
            var inside = Emit(new BuiltinInst(compare, SilType.Bool, current, upper));
            Jump(new CondBranchInst(inside, body, [], after, []));

            _current = body;
            BindLocal(loop.Element, Emit(new LoadInst(counter)), loop.Syntax);
            if (loop.Filter is { } filter) {
                var filtered = function.AddBlock();
                Condition(filter, filtered, step);
                _current = filtered;
            }

            InLoop(loop, after, step, () => Statement(loop.Body));
            if (_current is not null) {
                Branch(step);
            }

            // The last value of an inclusive range is the upper bound itself;
            // stopping there, rather than stepping past it, keeps 'a...Int.max'
            // from overflowing.
            _current = step;
            var last = Emit(new LoadInst(counter));
            var more = function.AddBlock();
            if (range.IsInclusive) {
                var atEnd = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, last, upper));
                Jump(new CondBranchInst(atEnd, after, [], more, []));
            } else {
                Branch(more);
            }

            _current = more;
            var one = Emit(new IntegerLiteralInst(SilType.Int, 1));
            Emit(new StoreInst(Emit(new BuiltinInst("add_Int", SilType.Int, last, one)), counter, StoreKind.Assign));
            Branch(header);

            _current = after;
        }

        /// <summary>
        ///     <c>for x in array</c>: a counting loop over the array, which is
        ///     kept in a slot of its own so that every way out of the loop
        ///     releases it with the rest.
        /// </summary>
        void ForArray(BoundForEach loop) {
            if (loop.Element is null || Expression(loop.Sequence) is not { Type: ArrayType arrayType } sequence
                || SlotFor(loop.Element, loop.Syntax) is null) {
                Fail(loop.Syntax, "this 'for' over an array");
                return;
            }

            var array = NewSlot(arrayType);
            Emit(new StoreInst(sequence, array, StoreKind.Assign));
            var counter = NewSlot(SilType.Int);
            Emit(new StoreInst(Emit(new IntegerLiteralInst(SilType.Int, 0)), counter, StoreKind.Init));

            var header = function.AddBlock();
            var body = function.AddBlock();
            var step = function.AddBlock();
            var after = function.AddBlock();
            Branch(header);

            _current = header;
            var current = Emit(new LoadInst(counter));
            var held = Emit(new LoadInst(array));
            var count = Emit(new BuiltinInst("array_count", SilType.Int, held));
            Destroy(held);
            Jump(new CondBranchInst(Emit(new BuiltinInst("cmp_lt_Int", SilType.Bool, current, count)), body, [], after, []));

            _current = body;
            var again = Emit(new LoadInst(array));
            var value = Emit(new BuiltinInst("array_get", arrayType.Element, again, Emit(new LoadInst(counter))));
            Destroy(again);
            BindLocal(loop.Element, value, loop.Syntax);
            if (loop.Filter is { } filter) {
                var filtered = function.AddBlock();
                Condition(filter, filtered, step);
                _current = filtered;
            }

            InLoop(loop, after, step, () => Statement(loop.Body));
            if (_current is not null) {
                Branch(step);
            }

            _current = step;
            var one = Emit(new IntegerLiteralInst(SilType.Int, 1));
            Emit(new StoreInst(Emit(new BuiltinInst("add_Int", SilType.Int, Emit(new LoadInst(counter)), one)), counter, StoreKind.Assign));
            Branch(header);

            _current = after;
        }

        void InLoop(BoundStatement loop, SilBlock breakTarget, SilBlock continueTarget, Action body) {
            var label = loop.Syntax.Parent is LabeledStatementSyntax { Label.Text: { Length: > 0 } text } ? text : null;
            _loops.Add(new(label, breakTarget, continueTarget, _scopes.Count, _temporaries.Count));
            body();
            _loops.RemoveAt(_loops.Count - 1);
        }

        Loop? FindLoop(string? label) {
            for (var i = _loops.Count - 1; i >= 0; i--) {
                if (label is null || _loops[i].Label == label) {
                    return _loops[i];
                }
            }

            return null;
        }

        void Do(BoundDo doStatement) {
            var after = function.AddBlock();

            if (doStatement.Catches.IsEmpty) {
                Statement(doStatement.Body);
                if (_current is not null) {
                    Branch(after);
                }

                _current = after;
                return;
            }

            // The catches all see one error type: what the body can throw.
            var errorType = ErrorTypeOf(doStatement);
            if (errorType is null) {
                Fail(doStatement.Syntax, "a 'do' whose catches take an error type that has no SIL type yet");
                return;
            }

            var dispatch = function.AddBlock();
            var error = dispatch.AddArgument(errorType);

            _handlers.Add(new(dispatch, errorType, _scopes.Count, _temporaries.Count));
            Statement(doStatement.Body);
            _handlers.RemoveAt(_handlers.Count - 1);
            if (_current is not null) {
                Branch(after);
            }

            // Each catch in turn; one that does not match, or whose guard
            // fails, passes the error to the next, and the last to whatever
            // handles errors outside. The error lives until a catch is chosen.
            _current = dispatch;
            foreach (var clause in doStatement.Catches) {
                var handler = function.AddBlock();
                var next = function.AddBlock();

                Hold(error);
                if (clause.Pattern is { } pattern) {
                    Pattern(pattern, error, handler, next);
                } else {
                    Branch(handler);
                }

                _current = handler;
                if (clause.ErrorLocal is { } local) {
                    BindLocal(local, error.Type.IsTrivial ? error : Emit(new CopyValueInst(error)), clause.Syntax);
                }

                if (clause.Guard is { } guard) {
                    var guarded = function.AddBlock();
                    Condition(guard, guarded, next);
                    _current = guarded;
                }

                Unhold(error);
                Destroy(error);
                Statement(clause.Body);
                if (_current is not null) {
                    Branch(after);
                }

                _current = next;
            }

            // Nothing caught it: on to whatever handles errors outside — unless
            // the last catch takes everything, when nothing gets this far.
            // Where nothing outside could take it, the binder has checked
            // that the catches cover every error.
            var last = doStatement.Catches[^1];
            if (last.Pattern is null && last.Guard is null || _handlers.Count == 0 && function.Type.Thrown is null) {
                Jump(new UnreachableInst());
            } else {
                RouteError(error, doStatement.Syntax);
            }

            _current = after;
        }

        /// <summary>The error type a 'do' body throws, from its catches' implicit error local or patterns.</summary>
        SilType? ErrorTypeOf(BoundDo doStatement) {
            foreach (var clause in doStatement.Catches) {
                if (clause.ErrorLocal is { } local) {
                    return LowerType(local.Type);
                }

                if (clause.Pattern is { } pattern) {
                    return LowerType(pattern.InputType);
                }
            }

            return null;
        }

        // -------------------------------------------------------------------
        // Conditions and patterns
        // -------------------------------------------------------------------

        /// <summary>Evaluates a condition and branches on it, short-circuiting '&amp;&amp;', '||' and '!'.</summary>
        void Condition(BoundExpression condition, SilBlock whenTrue, SilBlock whenFalse) {
            switch (condition) {
                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction: {
                    var right = function.AddBlock();
                    Condition(conjunction.Left, right, whenFalse);
                    _current = right;
                    Condition(conjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalOr } disjunction: {
                    var right = function.AddBlock();
                    Condition(disjunction.Left, whenTrue, right);
                    _current = right;
                    Condition(disjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not } negation:
                    Condition(negation.Operand, whenFalse, whenTrue);
                    return;

                // 'if let x = e': matched when e holds a value, which x then is.
                case BoundConditionBinding { Local: { } local } binding: {
                    if (Expression(binding.Source) is not { } optional || SlotFor(local, binding.Syntax) is null) {
                        return;
                    }

                    var optionalType = optional.Type as EnumType;
                    var some = optionalType?.CaseIndex("Some") ?? -1;
                    if (optionalType is null || some < 0) {
                        Fail(binding.Syntax, "an 'if let' on something that is not optional");
                        return;
                    }

                    var bound = function.AddBlock();
                    var payload = Payload(bound, optionalType.Cases[some].Payload!);
                    Jump(new SwitchEnumInst(optional, [(some, bound)], Destroying(optional, whenFalse)));
                    _current = bound;
                    BindLocal(local, payload, binding.Syntax);
                    Destroy(optional);
                    Branch(whenTrue);
                    return;
                }

                // 'if case .Circle(let r) = shape'.
                case BoundConditionBinding { Pattern: { } pattern } binding: {
                    if (Expression(binding.Source) is { } value) {
                        Hold(value);
                        Pattern(pattern, value, Destroying(value, whenTrue), Destroying(value, whenFalse));
                        Unhold(value);
                    }

                    return;
                }

                default:
                    if (Expression(condition) is { } flag) {
                        Jump(new CondBranchInst(flag, whenTrue, [], whenFalse, []));
                    }

                    return;
            }
        }

        /// <summary>
        ///     Tests a value against a pattern, binding what it names, and goes
        ///     to <paramref name="matched" /> or <paramref name="failed" />.
        ///     The value is borrowed; a binding stores a copy.
        /// </summary>
        void Pattern(BoundPattern pattern, SilValue value, SilBlock matched, SilBlock failed) {
            if (_failed) {
                return;
            }

            switch (pattern) {
                case BoundWildcardPattern:
                    Branch(matched);
                    return;

                case BoundBindingPattern binding:
                    if (SlotFor(binding.Local, binding.Syntax) is not null) {
                        BindLocal(binding.Local, value, binding.Syntax);
                        Branch(matched);
                    }

                    return;

                case BoundLiteralPattern { Value: null } when value.Type is EnumType optional && optional.CaseIndex("None") is var none and >= 0:
                    Jump(new SwitchEnumInst(value, [(none, matched)], failed));
                    return;

                case BoundLiteralPattern literal: {
                    if (LiteralValue(literal.Value, literal.IsNegative, value.Type, literal.Syntax) is not { } constant) {
                        return;
                    }

                    var equal = Emit(new BuiltinInst($"cmp_eq_{value.Type}", SilType.Bool, value, constant));
                    Destroy(constant);
                    Jump(new CondBranchInst(equal, matched, [], failed, []));
                    return;
                }

                case BoundRangePattern { Lower: BoundLiteralPattern lower, Upper: BoundLiteralPattern upper } range: {
                    if (LiteralValue(lower.Value, lower.IsNegative, value.Type, range.Syntax) is not { } low
                        || LiteralValue(upper.Value, upper.IsNegative, value.Type, range.Syntax) is not { } high) {
                        return;
                    }

                    var aboveLow = function.AddBlock();
                    var atLeast = Emit(new BuiltinInst($"cmp_ge_{value.Type}", SilType.Bool, value, low));
                    Jump(new CondBranchInst(atLeast, aboveLow, [], failed, []));
                    _current = aboveLow;
                    var atMost = Emit(new BuiltinInst($"cmp_le_{value.Type}", SilType.Bool, value, high));
                    Jump(new CondBranchInst(atMost, matched, [], failed, []));
                    return;
                }

                case BoundTuplePattern tuple when value.Type is TupleType:
                    Sequence(
                        tuple.Elements.Select((element, i) => (element, (Func<SilValue>)(() => Emit(new TupleExtractInst(value, i))))),
                        matched,
                        failed
                    );
                    return;

                case BoundRecordPattern { TestsType: false } record when value.Type is StructType structType:
                    Sequence(
                        record.Fields.Select(f => (f.Pattern,
                            (Func<SilValue>)(() => Emit(new StructExtractInst(value, structType.FieldIndex(f.Field.Name)))))),
                        matched,
                        failed
                    );
                    return;

                case BoundUnionCasePattern unionCase when value.Type is EnumType enumType: {
                    var index = enumType.CaseIndex(unionCase.UnionCase.Name);
                    var target = function.AddBlock();
                    var payloadType = enumType.Cases[index].Payload;
                    var payload = payloadType is null ? null : Payload(target, payloadType);
                    Jump(new SwitchEnumInst(value, [(index, target)], failed));
                    _current = target;

                    if (payload is null || unionCase.Payload.IsEmpty) {
                        Branch(matched);
                    } else if (unionCase.Payload.Length == 1 && payloadType is not TupleType) {
                        Pattern(unionCase.Payload[0], payload, matched, failed);
                    } else {
                        Sequence(
                            unionCase.Payload.Select((sub, i) => (sub, (Func<SilValue>)(() => Emit(new TupleExtractInst(payload, i))))),
                            matched,
                            failed
                        );
                    }

                    return;
                }

                case BoundEnumCasePattern enumCase when value.Type is EnumType enumType:
                    Jump(new SwitchEnumInst(value, [(enumType.CaseIndex(enumCase.EnumCase.Name), matched)], failed));
                    return;

                case BoundOrPattern or: {
                    var right = function.AddBlock();
                    Pattern(or.Left, value, matched, right);
                    _current = right;
                    Pattern(or.Right, value, matched, failed);
                    return;
                }

                default:
                    Fail(pattern.Syntax, $"the pattern '{Describe(pattern.Syntax)}'");
                    return;
            }
        }

        /// <summary>Several sub-patterns that must all match, tested left to right.</summary>
        void Sequence(IEnumerable<(BoundPattern Pattern, Func<SilValue> Value)> parts, SilBlock matched, SilBlock failed) {
            var list = parts.ToList();
            if (list.Count == 0) {
                Branch(matched);
                return;
            }

            for (var i = 0; i < list.Count; i++) {
                var next = i == list.Count - 1 ? matched : function.AddBlock();
                Pattern(list[i].Pattern, list[i].Value(), next, failed);
                _current = next == matched ? null : next;
            }
        }

        // -------------------------------------------------------------------
        // Expressions
        // -------------------------------------------------------------------

        /// <summary>Lowers an expression to an owned value — or a trivial one, or <see cref="UnitValue" />.</summary>
        SilValue? Expression(BoundExpression expression) {
            if (_failed) {
                return null;
            }

            switch (expression) {
                case BoundLiteral literal:
                    return Literal(literal, literal.Type);

                case BoundInterpolatedString interpolated:
                    return Interpolation(interpolated);

                case BoundConversion { ConversionKind: ConversionKind.Identity } conversion:
                    return Expression(conversion.Operand);

                // A literal taking the type the context asked for: 'let x: F64 = 1'.
                case BoundConversion { ConversionKind: ConversionKind.LiteralTyping, Operand: BoundLiteral literal } conversion:
                    return Literal(literal, conversion.Type);

                // An array literal taking the kind of array the context asked for.
                case BoundConversion { ConversionKind: ConversionKind.LiteralTyping, Operand: BoundArray array } conversion:
                    return ArrayLiteral(array, conversion.Type);

                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap } conversion:
                    return Wrap(conversion);

                // 'x!': the value, or a trap.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol } conversion:
                    return Unwrap(conversion);

                case BoundConversion { ConversionKind: ConversionKind.Explicit } conversion:
                    return NumericConversion(conversion);

                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var parameter):
                    return Owned(parameter);

                case BoundLocalAccess access:
                    return LoadLocal(access);

                case BoundParameterAccess access when closure?.Captures.TryGetValue(access.Parameter, out var captured) == true:
                    return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, captured.Field))));

                case BoundClosure closureExpression:
                    return Closure(closureExpression);

                case BoundFunctionReference reference:
                    return FunctionValue(reference);

                case BoundIndirectCall indirect:
                    return IndirectCall(indirect);

                case BoundSelfAccess:
                    if (_selfValue is { } selfValue) {
                        return Owned(selfValue);
                    }

                    if ((_selfObject ?? _selfAddress) is { } selfSlot) {
                        return Emit(new LoadInst(selfSlot));
                    }

                    return CapturedSelf() ?? Missing(expression, "'self' here");

                case BoundAssignment assignment:
                    return Assignment(assignment);

                case BoundFieldAccess { Receiver: { } receiver, Field.IsComputed: true } access:
                    return ComputedProperty(access, receiver);

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } access:
                    return FieldAccess(access, receiver, field);

                case BoundTupleElementAccess element: {
                    if (Sub(element.Receiver.Type) is TupleTypeSymbol && !IsTrivialType(element.Receiver.Type) && Place(element) is { } place) {
                        return Emit(new LoadInst(place));
                    }

                    if (Borrow(element.Receiver) is not { Type: TupleType } elementOf) {
                        return Missing(expression, "this tuple element");
                    }

                    var extracted = Owned(Emit(new TupleExtractInst(elementOf, element.Index)));
                    End(elementOf);
                    return extracted;
                }

                case BoundIndexAccess index:
                    return IndexRead(index);

                case BoundTuple tuple: {
                    if (Evaluate(tuple.Elements) is not { } elements) {
                        return null;
                    }

                    return Emit(new TupleInst(new TupleType([..elements.Select(static e => e.Type)]), [..elements]));
                }

                case BoundArray array:
                    return ArrayLiteral(array, array.Type);

                case BoundRecordExpression record:
                    return Record(record);

                case BoundUnionCaseConstruction construction:
                    return UnionCase(construction.Type, construction.UnionCase.Name, construction.Arguments, expression);

                case BoundUnionCaseGroup group:
                    return UnionCase(group.Type, group.UnionCase.Name, [], expression);

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr }:
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not, Type: var notType } when notType.Equals(Core.Bool):
                    return BoolFromBranch(expression);

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Coalesce } coalesce:
                    return Coalesce(coalesce);

                case BoundBinaryOperator binary when binary.Method is null:
                    return Binary(binary);

                case BoundUnaryOperator unary:
                    return Unary(unary);

                case BoundConditional conditional: {
                    if (LowerType(conditional.Type) is not { } type) {
                        return Missing(conditional, $"a '?:' of type '{conditional.Type.ToDisplayString()}'");
                    }

                    var whenTrue = function.AddBlock();
                    var whenFalse = function.AddBlock();
                    var after = function.AddBlock();
                    var result = after.AddArgument(type);
                    Condition(conditional.Condition, whenTrue, whenFalse);

                    _current = whenTrue;
                    if (Expression(conditional.WhenTrue) is { } yes) {
                        Branch(after, yes);
                    }

                    _current = whenFalse;
                    if (Expression(conditional.WhenFalse) is { } no) {
                        Branch(after, no);
                    }

                    _current = after;
                    return result;
                }

                case BoundMatch match:
                    return Match(match);

                case BoundTry tryExpression:
                    return Try(tryExpression);

                case BoundCall call:
                    return Call(call);

                default:
                    return Missing(expression, $"the expression '{Describe(expression.Syntax)}' ({expression.GetType().Name}{(expression is BoundConversion c ? " " + c.ConversionKind : "")})");
            }
        }

        bool IsTrivialType(TypeSymbol type) => LowerType(type)?.IsTrivial ?? false;

        SilValue? Missing(BoundNode node, string what) {
            Fail(node.Syntax, what);
            return null;
        }

        SilValue? Assignment(BoundAssignment assignment) {
            if (Expression(assignment.Value) is not { } value) {
                return null;
            }

            Hold(value);

            // 'a[i] = v': the array, made unique, takes the value.
            if (assignment.Target is BoundIndexAccess { Indices: [var indexExpression] } indexAccess
                && Sub(indexAccess.Receiver.Type) is ArrayTypeSymbol) {
                var ends = new List<SilValue>();
                if (Address(indexAccess.Receiver, ends) is not { } arrayAddress || Expression(indexExpression) is not { } index) {
                    return null;
                }

                Unhold(value);
                Emit(new BuiltinInst("array_set", SilType.Unit, arrayAddress, index, value));
                EndAll(ends);
                return UnitValue.Instance;
            }

            var borrows = new List<SilValue>();
            if (Address(assignment.Target, borrows) is not { } address) {
                return null;
            }

            Unhold(value);
            Emit(new StoreInst(value, address, StoreKind.Assign));
            EndAll(borrows);
            return value.Type.IsTrivial ? value : UnitValue.Instance;
        }

        void EndAll(List<SilValue> borrows) {
            for (var i = borrows.Count - 1; i >= 0; i--) {
                End(borrows[i]);
            }
        }

        SilValue? FieldAccess(BoundFieldAccess access, BoundExpression receiver, FieldSymbol field) {
            // A field of an object: through its address, while the object is borrowed.
            if (Sub(receiver.Type).TypeKind == TypeKind.Class) {
                if (Borrow(receiver) is not { Type: ClassType classType } reference) {
                    return null;
                }

                var index = classType.FieldIndex(field.Name);
                if (index < 0) {
                    return Missing(access, $"the member '{field.Name}'");
                }

                var loaded = Emit(new LoadInst(Emit(new RefElementAddrInst(reference, index))));
                End(reference);
                return loaded;
            }

            // A field of something in memory, read without copying the whole of it.
            if (!IsTrivialType(receiver.Type) && !IsBorrowable(access) && Place(access) is { } place) {
                return Emit(new LoadInst(place));
            }

            if (!IsTrivialType(receiver.Type)) {
                if (Borrow(receiver) is not { Type: StructType borrowedType } borrowed) {
                    return null;
                }

                var borrowedIndex = borrowedType.FieldIndex(field.Name);
                if (borrowedIndex < 0) {
                    return Missing(access, $"the member '{field.Name}'");
                }

                var copy = Owned(Emit(new StructExtractInst(borrowed, borrowedIndex)));
                End(borrowed);
                return copy;
            }

            if (Expression(receiver) is not { } value) {
                return null;
            }

            var fieldIndex = (value.Type as StructType)?.FieldIndex(field.Name) ?? -1;
            return fieldIndex < 0
                ? Missing(access, $"the member '{field.Name}'")
                : Emit(new StructExtractInst(value, fieldIndex));
        }

        /// <summary>
        ///     The address of a local, of <c>self</c> in memory, or of a field or
        ///     element inside one — reached without evaluating anything — or null.
        /// </summary>
        SilValue? Place(BoundExpression expression) {
            switch (expression) {
                case BoundLocalAccess local when _slots.TryGetValue(local.Local, out var slot) && !_boxed.Contains(local.Local):
                    return slot;

                case BoundSelfAccess when _selfAddress is { } self:
                    return self;

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when Sub(receiver.Type).TypeKind == TypeKind.Struct: {
                    if (Place(receiver) is not { Type: AddressType { Pointee: StructType structType } } address) {
                        return null;
                    }

                    var index = structType.FieldIndex(field.Name);
                    return index < 0 ? null : Emit(new StructElementAddrInst(address, index));
                }

                case BoundTupleElementAccess element when Place(element.Receiver) is { Type: AddressType { Pointee: TupleType } } tuple:
                    return Emit(new TupleElementAddrInst(tuple, element.Index));

                default:
                    return null;
            }
        }

        /// <summary>
        ///     Where an assignment stores: a local, <c>self</c>, or a field or
        ///     element inside one, or a field of an object. An object reached
        ///     on the way is borrowed until the caller ends <paramref name="borrows" />.
        /// </summary>
        SilValue? Address(BoundExpression target, List<SilValue> borrows) {
            switch (target) {
                case BoundLocalAccess local:
                    return LocalAddress(local.Local, local.Syntax, borrows);

                case BoundSelfAccess when _selfAddress is { } self:
                    return self;

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when Sub(receiver.Type).TypeKind == TypeKind.Class: {
                    if (Borrow(receiver) is not { Type: ClassType classType } reference) {
                        return null;
                    }

                    borrows.Add(reference);
                    var index = classType.FieldIndex(field.Name);
                    return index < 0
                        ? Missing(target, $"storing to '{field.Name}'")
                        : Emit(new RefElementAddrInst(reference, index));
                }

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }: {
                    if (Address(receiver, borrows) is not { } address) {
                        return null;
                    }

                    var structType = (address.Type as AddressType)?.Pointee as StructType;
                    var index = structType?.FieldIndex(field.Name) ?? -1;
                    return index < 0
                        ? Missing(target, $"storing to '{field.Name}'")
                        : Emit(new StructElementAddrInst(address, index));
                }

                case BoundTupleElementAccess element:
                    return Address(element.Receiver, borrows) is { } tuple
                        ? Emit(new TupleElementAddrInst(tuple, element.Index))
                        : null;

                default:
                    return Missing(target, $"storing to '{Describe(target.Syntax)}'");
            }
        }

        SilValue? Literal(BoundLiteral literal, TypeSymbol type) {
            // 'none': the optional with nothing in it.
            if (literal.Value is null && LowerType(type) is EnumType optional && optional.CaseIndex("None") is var none and >= 0) {
                return Emit(new EnumInst(optional, none, null));
            }

            // A plain enum's case is bound as a literal naming it.
            if (literal.Value is string caseName && type is NamedTypeSymbol { TypeKind: TypeKind.Enum }) {
                return LowerType(type) is EnumType enumType && enumType.CaseIndex(caseName) is var index and >= 0
                    ? Emit(new EnumInst(enumType, index, null))
                    : Missing(literal, $"the enum case '{caseName}'");
            }

            if (LowerType(type) is not { } silType) {
                return Missing(literal, $"a literal of type '{type.ToDisplayString()}'");
            }

            return LiteralValue(literal.Value, false, silType, literal.Syntax);
        }

        SilValue? LiteralValue(object? value, bool negative, SilType type, SyntaxNode at) {
            if (type == SilType.String && value is string text) {
                return Emit(new StringLiteralInst(text));
            }

            if (type == SilType.Bool && value is bool flag) {
                return Emit(new IntegerLiteralInst(SilType.Bool, flag ? 1 : 0));
            }

            if (type == SilType.Char && value is Rune scalar) {
                return Emit(new IntegerLiteralInst(SilType.Char, scalar.Value));
            }

            if (type == SilType.Int && value is long or int or ulong) {
                var number = System.Convert.ToDecimal(value);
                if (negative) {
                    number = -number;
                }

                if (number is > long.MaxValue or < long.MinValue) {
                    Fail(at, $"the integer '{value}', which does not fit in Int");
                    return null;
                }

                return Emit(new IntegerLiteralInst(SilType.Int, (long)number));
            }

            if (type == SilType.F64 && value is double or long or int or ulong) {
                var number = System.Convert.ToDouble(value);
                return Emit(new FloatLiteralInst(negative ? -number : number));
            }

            Fail(at, $"the literal '{Describe(at)}' of type {type}");
            return null;
        }

        /// <summary>
        ///     <c>"x = \(x)"</c>: the text and the holes, each as a string,
        ///     joined left to right.
        /// </summary>
        SilValue? Interpolation(BoundInterpolatedString interpolated) {
            if (interpolated.Syntax is not InterpolatedStringExpressionSyntax syntax) {
                return Missing(interpolated, "this interpolated string");
            }

            SilValue? result = null;
            var hole = 0;
            foreach (var content in syntax.Contents) {
                SilValue? piece;
                if (content is InterpolatedTextSyntax text) {
                    var value = text.TextToken.Value as string ?? "";
                    if (value.Length == 0) {
                        continue;
                    }

                    piece = Emit(new StringLiteralInst(value));
                } else {
                    if (hole >= interpolated.Parts.Length || Expression(interpolated.Parts[hole]) is not { } part) {
                        return null;
                    }

                    piece = Stringify(part, interpolated.Parts[hole]);
                    hole++;
                    if (piece is null) {
                        return null;
                    }
                }

                if (result is null) {
                    result = Hold(piece);
                    continue;
                }

                var joined = Emit(new BuiltinInst("string_concat", SilType.String, result, piece));
                Destroy(piece);
                End(result);
                result = Hold(joined);
            }

            return result is null ? Emit(new StringLiteralInst("")) : Unhold(result);
        }

        /// <summary>A value as a string, for interpolation: a string itself, or the scalars written out.</summary>
        SilValue? Stringify(SilValue value, BoundNode at) {
            if (value.Type == SilType.String) {
                return value;
            }

            var name = $"string_from_{value.Type}";
            return SilBuiltins.Find(name) is not null
                ? Emit(new BuiltinInst(name, SilType.String, value))
                : Missing(at, $"interpolating a value of type {value.Type}");
        }

        SilValue? Wrap(BoundConversion conversion) {
            if (LowerType(conversion.Type) is not EnumType optional) {
                return Missing(conversion, $"an optional of '{conversion.Operand.Type.ToDisplayString()}'");
            }

            // A literal wrapped straight into an optional takes the wrapped type.
            var inner = conversion.Operand is BoundLiteral literal
                ? Literal(literal, ((OptionalTypeSymbol)conversion.Type).UnderlyingType)
                : Expression(conversion.Operand);

            return inner is null ? null : Emit(new EnumInst(optional, optional.CaseIndex("Some"), inner));
        }

        SilValue? Unwrap(BoundConversion conversion) {
            if (Expression(conversion.Operand) is not { Type: EnumType optional } value) {
                return null;
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[some].Payload!);
            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "unwrapped a none"));
            Jump(new UnreachableInst());

            _current = present;
            var result = Owned(payload);
            Destroy(value);
            return result;
        }

        SilValue? NumericConversion(BoundConversion conversion) {
            if (Expression(conversion.Operand) is not { } value || LowerType(conversion.Type) is not { } target) {
                return null;
            }

            if (value.Type == target) {
                return value;
            }

            var name = $"convert_{value.Type}_{target}";
            return SilBuiltins.Find(name) is not null
                ? Emit(new BuiltinInst(name, target, value))
                : Missing(conversion, $"the conversion from {value.Type} to {target}");
        }

        SilValue? Record(BoundRecordExpression record) {
            if (LowerType(record.Type) is not StructType structType) {
                return Missing(record, $"a record of type '{record.Type.ToDisplayString()}'");
            }

            if (Evaluate(record.Fields.Select(static f => f.Value)) is not { } values) {
                return null;
            }

            var fields = new SilValue?[structType.Fields.Count];
            for (var i = 0; i < values.Count; i++) {
                fields[structType.FieldIndex(record.Fields[i].Field.Name)] = values[i];
            }

            if (fields.Any(static f => f is null)) {
                values.ForEach(Destroy);
                return Missing(record, "a record that leaves a field to its default");
            }

            return Emit(new StructInst(structType, [..fields!]));
        }

        SilValue? UnionCase(TypeSymbol unionType, string name, IReadOnlyList<BoundExpression> arguments, BoundNode at) {
            var enumType = LowerType(unionType) as EnumType;
            var index = enumType?.CaseIndex(name) ?? -1;
            if (enumType is null || index < 0) {
                return Missing(at, $"the case '{name}'");
            }

            if (Evaluate(arguments) is not { } values) {
                return null;
            }

            SilValue? payload = values.Count switch {
                0 => null,
                1 when enumType.Cases[index].Payload is not TupleType => values[0],
                _ => Emit(new TupleInst((TupleType)enumType.Cases[index].Payload!, [..values])),
            };

            return Emit(new EnumInst(enumType, index, payload));
        }

        /// <summary><c>[a, b]</c>: an empty array in a slot of its own, each element appended, then taken out.</summary>
        SilValue? ArrayLiteral(BoundArray array, TypeSymbol type) {
            if (LowerType(type) is not ArrayType arrayType) {
                return Missing(array, $"an array of type '{type.ToDisplayString()}'");
            }

            var empty = Emit(new BuiltinInst("array_empty", arrayType));
            if (array.Elements.IsEmpty) {
                return empty;
            }

            var slot = NewSlot(arrayType);
            Emit(new StoreInst(empty, slot, StoreKind.Assign));
            foreach (var element in array.Elements) {
                if (Expression(element) is not { } value) {
                    return null;
                }

                Emit(new BuiltinInst("array_append", SilType.Unit, slot, value));
            }

            return Emit(new LoadInst(slot, LoadKind.Take));
        }

        SilValue? IndexRead(BoundIndexAccess index) {
            if (Sub(index.Receiver.Type) is not ArrayTypeSymbol || index.Indices is not [var indexExpression]) {
                return Missing(index, $"indexing '{index.Receiver.Type.ToDisplayString()}'");
            }

            if (Borrow(index.Receiver) is not { Type: ArrayType arrayType } array) {
                return null;
            }

            if (Expression(indexExpression) is not { } position) {
                return null;
            }

            var element = Emit(new BuiltinInst("array_get", arrayType.Element, array, position));
            End(array);
            return element;
        }

        /// <summary>'a ?? b': a's value when there is one; b, evaluated only then, when there is not.</summary>
        SilValue? Coalesce(BoundBinaryOperator coalesce) {
            if (Expression(coalesce.Left) is not { Type: EnumType optional } value || LowerType(coalesce.Type) is not { } type) {
                return Missing(coalesce, "this '??'");
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[some].Payload!);
            var absent = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(type);
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = present;
            var unwrapped = Owned(payload);
            Destroy(value);
            Branch(after, unwrapped);

            _current = absent;
            Destroy(value);
            if (Expression(coalesce.Right) is { } fallback) {
                Branch(after, fallback);
            }

            _current = after;
            return result;
        }

        /// <summary>A Bool computed by branching: '&amp;&amp;', '||' and '!' as values.</summary>
        SilValue BoolFromBranch(BoundExpression condition) {
            var whenTrue = function.AddBlock();
            var whenFalse = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(SilType.Bool);
            Condition(condition, whenTrue, whenFalse);

            _current = whenTrue;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 1)));
            _current = whenFalse;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 0)));

            _current = after;
            return result;
        }

        SilValue? Binary(BoundBinaryOperator binary) {
            // 'x == none': only the case matters, so any optional compares.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
                && (IsNone(binary.Right) ? binary.Left : IsNone(binary.Left) ? binary.Right : null) is { } tested) {
                if (Borrow(tested) is not { Type: EnumType testedType } testedValue) {
                    return Missing(binary, "this comparison with 'none'");
                }

                var noneCase = testedType.CaseIndex("None");
                var isNone = function.AddBlock();
                var isSome = function.AddBlock();
                var joined = function.AddBlock();
                var answer = joined.AddArgument(SilType.Bool);
                var equal = binary.OperatorKind == BinaryOperatorKind.Equal;
                Jump(new SwitchEnumInst(testedValue, [(noneCase, isNone)], isSome));
                _current = isNone;
                Branch(joined, Emit(new IntegerLiteralInst(SilType.Bool, equal ? 1 : 0)));
                _current = isSome;
                Branch(joined, Emit(new IntegerLiteralInst(SilType.Bool, equal ? 0 : 1)));
                _current = joined;
                End(testedValue);
                return answer;
            }

            if (Borrow(binary.Left) is not { } left) {
                return null;
            }

            if (Borrow(binary.Right) is not { } right) {
                End(left);
                return null;
            }

            // An optional compared with a plain value: the value, wrapped.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual) {
                if (left.Type is EnumType { Cases: [("Some", { } leftPayload), ("None", null)] } leftOptional && right.Type == leftPayload) {
                    right = Hold(Emit(new EnumInst(leftOptional, 0, Owned(Unhold(right)))));
                } else if (right.Type is EnumType { Cases: [("Some", { } rightPayload), ("None", null)] } rightOptional && left.Type == rightPayload) {
                    // Left was borrowed first; keep the temporaries in order.
                    Unhold(right);
                    left = Emit(new EnumInst(rightOptional, 0, Owned(Unhold(left))));
                    Hold(left);
                    Hold(right);
                }
            }

            // Optionals compare equal when both are none, or both hold equal values.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
                && left.Type is EnumType { Cases: [("Some", { } payloadType), ("None", null)] } optional
                && right.Type == left.Type) {
                var equal = OptionalEquals(optional, payloadType, left, right, binary);
                End(right);
                End(left);
                return equal is null || binary.OperatorKind == BinaryOperatorKind.Equal
                    ? equal
                    : Emit(new BuiltinInst("not_Bool", SilType.Bool, equal));
            }

            var op = binary.OperatorKind switch {
                BinaryOperatorKind.Add when left.Type == SilType.String => "string_concat",
                BinaryOperatorKind.Add => "add",
                BinaryOperatorKind.Subtract => "sub",
                BinaryOperatorKind.Multiply => "mul",
                BinaryOperatorKind.Divide => "div",
                BinaryOperatorKind.Remainder => "rem",
                BinaryOperatorKind.And => "and",
                BinaryOperatorKind.Or => "or",
                BinaryOperatorKind.ExclusiveOr => "xor",
                BinaryOperatorKind.LeftShift => "shl",
                BinaryOperatorKind.RightShift => "shr",
                BinaryOperatorKind.Equal => "cmp_eq",
                BinaryOperatorKind.NotEqual => "cmp_ne",
                BinaryOperatorKind.LessThan => "cmp_lt",
                BinaryOperatorKind.LessThanOrEqual => "cmp_le",
                BinaryOperatorKind.GreaterThan => "cmp_gt",
                BinaryOperatorKind.GreaterThanOrEqual => "cmp_ge",
                _ => null,
            };

            var name = op is null or "string_concat" ? op : $"{op}_{left.Type}";
            if (name is null || SilBuiltins.Find(name) is not { } signature || left.Type != right.Type) {
                End(right);
                End(left);
                return Missing(binary, $"the operator in '{Describe(binary.Syntax)}' on '{binary.Left.Type.ToDisplayString()}'");
            }

            var result = Emit(new BuiltinInst(name, signature.Result, left, right));
            End(right);
            End(left);
            return result;
        }

        static bool IsNone(BoundExpression expression) =>
            expression switch {
                BoundLiteral { Value: null } => true,
                BoundConversion conversion => IsNone(conversion.Operand),
                _ => false,
            };

        /// <summary>Two optionals of a comparable type compared: by case, and then by value.</summary>
        SilValue? OptionalEquals(EnumType optional, SilType payloadType, SilValue left, SilValue right, BoundNode at) {
            var compare = $"cmp_eq_{payloadType}";
            if (SilBuiltins.Find(compare) is null) {
                return Missing(at, $"comparing optionals of {payloadType}");
            }

            var some = optional.CaseIndex("Some");
            var leftSome = function.AddBlock();
            var leftValue = Payload(leftSome, payloadType);
            var leftNone = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(SilType.Bool);
            Jump(new SwitchEnumInst(left, [(some, leftSome)], leftNone));

            // Left holds a value: equal only if right holds an equal one.
            _current = leftSome;
            var bothSome = function.AddBlock();
            var rightValue = Payload(bothSome, payloadType);
            var differ = function.AddBlock();
            Jump(new SwitchEnumInst(right, [(some, bothSome)], differ));
            _current = bothSome;
            Branch(after, Emit(new BuiltinInst(compare, SilType.Bool, leftValue, rightValue)));
            _current = differ;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 0)));

            // Left is none: equal only if right is too.
            _current = leftNone;
            var rightSome = function.AddBlock();
            Payload(rightSome, payloadType);
            var bothNone = function.AddBlock();
            Jump(new SwitchEnumInst(right, [(some, rightSome)], bothNone));
            _current = rightSome;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 0)));
            _current = bothNone;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 1)));

            _current = after;
            return result;
        }

        SilValue? Unary(BoundUnaryOperator unary) {
            if (unary.OperatorKind == UnaryOperatorKind.Plus) {
                return Expression(unary.Operand);
            }

            if (Expression(unary.Operand) is not { } operand) {
                return null;
            }

            var op = unary.OperatorKind switch {
                UnaryOperatorKind.Negate => "neg",
                UnaryOperatorKind.Complement => "not",
                _ => null,
            };

            var name = op is null ? null : $"{op}_{operand.Type}";
            if (name is null || SilBuiltins.Find(name) is not { } signature) {
                return Missing(unary, $"the operator in '{Describe(unary.Syntax)}'");
            }

            return Emit(new BuiltinInst(name, signature.Result, operand));
        }

        /// <summary>
        ///     A <c>match</c>: arms tried in order, each pattern in turn, then the
        ///     guard. The binder has checked it is exhaustive, so failing every
        ///     arm cannot happen. The subject lives until an arm is chosen.
        /// </summary>
        SilValue? Match(BoundMatch match) {
            if (Expression(match.Subject) is not { } subject) {
                return null;
            }

            var resultType = LowerType(match.Type);
            var producesValue = resultType is not null && resultType != SilType.Unit && !match.Type.Equals(Core.Never);
            var after = function.AddBlock();
            var result = producesValue ? after.AddArgument(resultType!) : null;

            foreach (var arm in match.Arms) {
                var body = function.AddBlock();
                var nextArm = function.AddBlock();

                Hold(subject);
                if (arm.IsDefault) {
                    Branch(body);
                } else {
                    for (var i = 0; i < arm.Patterns.Length; i++) {
                        var nextPattern = i == arm.Patterns.Length - 1 ? nextArm : function.AddBlock();
                        Pattern(arm.Patterns[i], subject, body, nextPattern);
                        _current = nextPattern;
                    }
                }

                _current = body;
                if (arm.Guard is { } guard) {
                    var guarded = function.AddBlock();
                    Condition(guard, guarded, nextArm);
                    _current = guarded;
                }

                Unhold(subject);
                Destroy(subject);

                switch (arm.Body) {
                    case BoundStatement statement:
                        Statement(statement);
                        if (_current is not null) {
                            if (producesValue) {
                                Jump(new UnreachableInst());
                            } else {
                                Branch(after);
                            }
                        }

                        break;

                    case BoundExpression value:
                        if (Expression(value) is { } armValue && _current is not null) {
                            if (!producesValue) {
                                Destroy(armValue);
                                Branch(after);
                            } else if (armValue is not UnitValue && armValue.Type == resultType) {
                                Branch(after, armValue);
                            } else {
                                Jump(new UnreachableInst());
                            }
                        }

                        break;
                }

                _current = nextArm;
            }

            Jump(new UnreachableInst());
            _current = after;
            return result ?? (SilValue)UnitValue.Instance;
        }

        /// <summary>
        ///     <c>try e</c> lets errors go to the handler in force; the others
        ///     put a handler of their own around <c>e</c>.
        /// </summary>
        SilValue? Try(BoundTry tryExpression) {
            if (tryExpression.TryKind == TryKind.Propagate) {
                return Expression(tryExpression.Expression);
            }

            var errorType = ThrownBy(tryExpression.Expression);
            if (errorType is null) {
                return Missing(tryExpression, "a 'try' whose error type has no SIL type yet");
            }

            var resultType = LowerType(tryExpression.Type);
            if (resultType is null) {
                return Missing(tryExpression, $"a 'try' of type '{tryExpression.Type.ToDisplayString()}'");
            }

            var handler = function.AddBlock();
            var error = handler.AddArgument(errorType);
            var after = function.AddBlock();
            var result = resultType == SilType.Unit ? null : after.AddArgument(resultType);

            _handlers.Add(new(handler, errorType, _scopes.Count, _temporaries.Count));
            var value = Expression(tryExpression.Expression);
            _handlers.RemoveAt(_handlers.Count - 1);

            if (value is not null && _current is not null) {
                if (tryExpression.TryKind == TryKind.Optional) {
                    var optional = (EnumType)resultType;
                    Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("Some"), value)));
                } else if (result is null) {
                    Branch(after);
                } else {
                    Branch(after, value);
                }
            }

            _current = handler;
            switch (tryExpression.TryKind) {
                case TryKind.Optional: {
                    Destroy(error);
                    var optional = (EnumType)resultType;
                    Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("None"), null)));
                    break;
                }

                case TryKind.Force:
                    Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "'try!' caught an error"));
                    Jump(new UnreachableInst());
                    break;

                case TryKind.Fallback when tryExpression.Fallback is { } fallback: {
                    if (tryExpression.ErrorLocal is { } local) {
                        BindLocal(local, error, tryExpression.Syntax);
                    } else {
                        Destroy(error);
                    }

                    var alternative = Expression(fallback);
                    if (alternative is not null && _current is not null) {
                        if (result is null) {
                            Destroy(alternative);
                            Branch(after);
                        } else {
                            Branch(after, alternative);
                        }
                    }

                    break;
                }
            }

            _current = after;
            return result ?? (SilValue)UnitValue.Instance;
        }

        /// <summary>The error type of the first throwing call inside an expression.</summary>
        SilType? ThrownBy(BoundNode node) {
            if (node is BoundCall { Function.ThrownType: { } thrown }) {
                return LowerType(thrown);
            }

            if (node is BoundIndirectCall { Callee.Type: FunctionTypeSymbol { ThrownType: { } indirectThrown } }) {
                return LowerType(indirectThrown);
            }

            foreach (var child in node.Children) {
                if (child is BoundClosure) {
                    continue;
                }

                if (ThrownBy(child) is { } found) {
                    return found;
                }
            }

            return null;
        }

        // -------------------------------------------------------------------
        // Calls
        // -------------------------------------------------------------------

        SilValue? Call(BoundCall call) {
            Locate(call.Syntax);

            // The core's print is a builtin: the runtime owns the writing.
            if (Core.Print.Contains(call.Function)) {
                if (Borrow(call.Arguments[0]) is not { } printed) {
                    return null;
                }

                Emit(new BuiltinInst($"print_{printed.Type}", SilType.Unit, printed));
                End(printed);
                return UnitValue.Instance;
            }

            var callee = call.Function;

            // A member of a core type: a builtin.
            if (CoreMember(call) is { } handled) {
                return handled.Value;
            }

            // A synthesised memberwise initialiser is the struct, built from its fields.
            if (callee is { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true }) {
                if (LowerType(call.Type) is not StructType structType) {
                    return Missing(call, $"initialising '{Sub(call.Type).ToDisplayString()}'");
                }

                if (Evaluate(call.Arguments) is not { } fields) {
                    return null;
                }

                if (fields.Count != structType.Fields.Count) {
                    fields.ForEach(Destroy);
                    return Missing(call, "a memberwise initialiser that leaves a field to its default");
                }

                return Emit(new StructInst(structType, [..fields]));
            }

            // A protocol's requirement, called on a type parameter: the
            // concrete type's own member, now that the type is known.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && Witness(call) is { } witness) {
                callee = witness;
            }

            string name;
            FunctionType? type;
            if (IsGeneric(callee)) {
                if (generator.Instance(callee, CallSubstitution(call, callee), call.Syntax) is not { } instance) {
                    _failed = true;
                    return null;
                }

                (name, type) = instance;
            } else {
                if (!generator._names.TryGetValue(callee, out var plainName)) {
                    return Missing(call, $"the call '{Describe(call.Syntax)}'");
                }

                name = plainName;
                type = generator._module.Find(name)?.Type ?? generator.SignatureOf(callee, call.Syntax, TypeSubstitution.Empty);
                if (type is null) {
                    _failed = true;
                    return null;
                }
            }

            // Everything passed is borrowed for the call: a parameter as it
            // is, anything evaluated as a temporary destroyed after.
            var arguments = new List<SilValue>();
            var borrows = new List<SilValue>();
            if (HasSelf(callee)) {
                SilValue? self;
                if (type.Parameters[0] is AddressType) {
                    self = call.Receiver is null or BoundSelfAccess ? _selfAddress : Address(call.Receiver, borrows);
                } else if (call.Receiver is null) {
                    self = _selfValue ?? ((_selfObject ?? _selfAddress) is { } address ? Hold(Emit(new LoadInst(address))) : null)
                        ?? (CapturedSelf() is { } captured ? Hold(captured) : null);
                    if (self is not null && IsOwned(self)) {
                        borrows.Add(self);
                    }
                } else {
                    self = Borrow(call.Receiver);
                    if (self is not null) {
                        borrows.Add(self);
                    }
                }

                if (self is null) {
                    return Missing(call, "the receiver of this call");
                }

                arguments.Add(self);
            }

            foreach (var argument in call.Arguments) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            return Invoke(Emit(new FunctionRefInst(name, type)), type, arguments, borrows, call.Syntax);
        }

        /// <summary>
        ///     Calls a function or a closure with borrowed arguments: <c>apply</c>,
        ///     or <c>try_apply</c> with the borrows ended on both of its edges.
        /// </summary>
        SilValue? Invoke(SilValue reference, FunctionType type, List<SilValue> arguments, List<SilValue> borrows, SyntaxNode at) {
            if (type.Thrown is null) {
                var apply = Emit(new ApplyInst(reference, type.Result, [..arguments]));
                EndAll(borrows);
                return type.Result == SilType.Unit ? UnitValue.Instance : apply;
            }

            // A throwing call: on to the normal block with the result, or the
            // error to whatever handles it here. Its borrows end on both.
            var normal = function.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var errorBlock = function.AddBlock();
            var error = errorBlock.AddArgument(type.Thrown);
            for (var i = borrows.Count - 1; i >= 0; i--) {
                Unhold(borrows[i]);
            }

            Jump(new TryApplyInst(reference, type.Result, [..arguments], normal, errorBlock));

            _current = errorBlock;
            for (var i = borrows.Count - 1; i >= 0; i--) {
                Destroy(borrows[i]);
            }

            RouteError(error, at);

            _current = normal;
            for (var i = borrows.Count - 1; i >= 0; i--) {
                Destroy(borrows[i]);
            }

            return result ?? (SilValue)UnitValue.Instance;
        }

        /// <summary>
        ///     What the callee's type parameters stand for at a call, in this
        ///     instance: what the binder inferred, seen through this instance's
        ///     own substitution; for a member, what the receiver's type says;
        ///     failing both, what they stand for here — a call from inside the
        ///     same generic type.
        /// </summary>
        TypeSubstitution CallSubstitution(BoundCall call, FunctionSymbol callee) {
            var receiver = call.Receiver is { } r ? TypeSubstitution.For(Sub(r.Type)) : TypeSubstitution.Empty;
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            foreach (var parameter in TypeParametersOf(callee)) {
                var bound = call.Substitution.Lookup(parameter) is { } inferred
                    ? Sub(inferred)
                    : receiver.Lookup(parameter) ?? substitution.Lookup(parameter);
                if (bound is not null) {
                    map[parameter] = bound;
                }
            }

            return new(map);
        }

        /// <summary>The member of the receiver's concrete type that implements a protocol requirement.</summary>
        FunctionSymbol? Witness(BoundCall call) {
            var requirement = call.Function;
            var self = call.Receiver is { } receiver ? Sub(receiver.Type) : null;
            if (self is not NamedTypeSymbol concrete) {
                return null;
            }

            return concrete.OriginalDefinition.Members.OfType<FunctionSymbol>().FirstOrDefault(f =>
                f.Name == requirement.Name
                && f.IsStatic == requirement.IsStatic
                && f.Parameters.Length == requirement.Parameters.Length
                && OwnerOf(f) is { TypeKind: not TypeKind.Protocol });
        }

        /// <summary><c>self</c>, as a closure captured it: a copy out of the context.</summary>
        SilValue? CapturedSelf() =>
            closure?.Captures.TryGetValue(SelfKey, out var capture) == true
                ? Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, capture.Field))))
                : null;

        /// <summary>
        ///     A closure: its code lowered as a function of its own, then its
        ///     context built — a box for each local captured by reference, a
        ///     copy of each value captured by value — and the two put together.
        /// </summary>
        SilValue? Closure(BoundClosure closureExpression) {
            if (LowerType(closureExpression.Type) is not ThickFunctionType thick) {
                return Missing(closureExpression, $"a closure of type '{closureExpression.Type.ToDisplayString()}'");
            }

            var captures = CapturesOf(closureExpression);
            var name = generator.ClosureName(function.Name);
            ClassType? contextType = null;
            if (captures.Count > 0) {
                contextType = generator._module.AddType(new ClassType($"{name}$context"));
                foreach (var capture in captures) {
                    if (LowerType(capture.Type) is not { } fieldType) {
                        return Missing(closureExpression, $"capturing a value of type '{capture.Type.ToDisplayString()}'");
                    }

                    contextType.Fields.Add(($"c{capture.Field}", capture.Boxed ? generator.BoxType(fieldType) : fieldType));
                }
            }

            // The code.
            var code = new SilFunction(name, thick.Implementation) {
                SourceFile = function.SourceFile,
                Line = source.GetLinePosition(closureExpression.Syntax.Span.Start).Line,
            };
            code.ParameterNames.AddRange(closureExpression.Symbol.Parameters.Select(static p => p.Name));
            var info = new ClosureInfo(contextType, captures.ToDictionary(static c => c.Key, static c => c));
            new FunctionLowering(generator, closureExpression.Symbol, code, source, substitution, info).Lower(closureExpression.Body);
            code.RemoveUnreachableBlocks();
            generator._module.Add(code);

            // The context.
            Locate(closureExpression.Syntax);
            var reference = Emit(new FunctionRefInst(name, thick.Implementation));
            if (contextType is null) {
                return Emit(new PartialApplyInst(reference, null, thick));
            }

            var context = Hold(Emit(new AllocRefInst(contextType)));
            foreach (var capture in captures) {
                if (CapturedValue(capture, closureExpression) is not { } value) {
                    return null;
                }

                Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(context, capture.Field)), StoreKind.Assign));
            }

            return Emit(new PartialApplyInst(reference, Unhold(context), thick));
        }

        /// <summary>What goes into a closure's context for one capture: a box, or a value.</summary>
        SilValue? CapturedValue(Capture capture, BoundNode at) {
            switch (capture.Key) {
                case LocalSymbol local when capture.Boxed:
                    // The box itself: from this function's slot, or from its own context.
                    if (closure is not null && closure.Captures.TryGetValue(local, out var outer) && outer.Boxed) {
                        return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, outer.Field))));
                    }

                    if (!_boxed.Contains(local) || SlotFor(local, at.Syntax) is not { } slot) {
                        return Missing(at, $"capturing '{local.Name}'");
                    }

                    return Emit(new LoadInst(slot));

                case LocalSymbol when capture.Initializer is { } initializer:
                    return Expression(initializer);

                case ParameterSymbol parameter when _parameters.TryGetValue(parameter, out var value):
                    return Owned(value);

                case ParameterSymbol parameter when closure?.Captures.TryGetValue(parameter, out var outerParameter) == true:
                    return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, outerParameter.Field))));

                case SelfKey:
                    return _selfValue is { } self ? Owned(self)
                        : (_selfObject ?? _selfAddress) is { } selfSlot ? Emit(new LoadInst(selfSlot))
                        : CapturedSelf() ?? Missing(at, "capturing 'self'");

                default:
                    return Missing(at, "this capture");
            }
        }

        /// <summary>A named function as a value: a closure over a thunk, with no context.</summary>
        SilValue? FunctionValue(BoundFunctionReference reference) {
            var callee = reference.Function;
            if (reference.Receiver is not null || IsGeneric(callee) || !generator._names.TryGetValue(callee, out var name)
                || LowerType(reference.Type) is not ThickFunctionType thick) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            var type = generator._module.Find(name)?.Type ?? generator.SignatureOf(callee, reference.Syntax, TypeSubstitution.Empty);
            if (type is null || type != thick.Signature) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            var thunk = generator.Thunk(name, type);
            return Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, thick.Implementation)), null, thick));
        }

        /// <summary>A call through a closure: the closure and the arguments, all borrowed for the call.</summary>
        SilValue? IndirectCall(BoundIndirectCall call) {
            Locate(call.Syntax);
            if (Borrow(call.Callee) is not { Type: ThickFunctionType thick } callee) {
                return Missing(call, "this call through a value");
            }

            var arguments = new List<SilValue>();
            var borrows = new List<SilValue> { callee };
            foreach (var argument in call.Arguments) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            return Invoke(callee, thick.Signature, arguments, borrows, call.Syntax);
        }

        /// <summary>A result, which may be nothing because lowering failed; null means "not a core member".</summary>
        sealed record Handled(SilValue? Value);

        /// <summary>
        ///     The members the core gives its own types — strings, numbers,
        ///     characters, arrays — as builtins on the receiver.
        /// </summary>
        Handled? CoreMember(BoundCall call) {
            var callee = call.Function;
            var owner = callee.ContainingSymbol;

            // String.fromChar(c): a static member, with no receiver.
            if (callee is { Name: "fromChar", IsStatic: true } && ReferenceEquals(owner, Core.String)) {
                return new(Expression(call.Arguments[0]) is { } c ? Emit(new BuiltinInst("string_from_Char", SilType.String, c)) : null);
            }

            if (call.Receiver is not { } receiver) {
                return null;
            }

            // Mutating array members work on the array where it is.
            if (Sub(receiver.Type) is ArrayTypeSymbol && callee.Name is "append" or "removeLast" or "clear") {
                var borrows = new List<SilValue>();
                if (Address(receiver, borrows) is not { Type: AddressType { Pointee: ArrayType arrayType } } address) {
                    return new((SilValue?)null);
                }

                SilValue? result;
                switch (callee.Name) {
                    case "append":
                        if (Expression(call.Arguments[0]) is not { } element) {
                            return new((SilValue?)null);
                        }

                        Emit(new BuiltinInst("array_append", SilType.Unit, address, element));
                        result = UnitValue.Instance;
                        break;

                    case "removeLast":
                        result = Emit(new BuiltinInst("array_remove_last", arrayType.Element, address));
                        break;

                    default:
                        Emit(new BuiltinInst("array_clear", SilType.Unit, address));
                        result = UnitValue.Instance;
                        break;
                }

                EndAll(borrows);
                return new(result);
            }

            var isCore = Sub(receiver.Type) is ArrayTypeSymbol
                || Sub(receiver.Type).Equals(Core.String) || Sub(receiver.Type).Equals(Core.Bool) || Sub(receiver.Type).Equals(Core.Char)
                || Core.Numerics.Any(n => n.Equals(Sub(receiver.Type)));
            if (!isCore) {
                return null;
            }

            if (Borrow(receiver) is not { } self) {
                return new((SilValue?)null);
            }

            var arguments = new List<SilValue>();
            foreach (var argument in call.Arguments) {
                if (Borrow(argument) is not { } value) {
                    return new((SilValue?)null);
                }

                arguments.Add(value);
            }

            var lowered = CoreMethod(callee.Name, self, arguments, call);
            for (var i = arguments.Count - 1; i >= 0; i--) {
                End(arguments[i]);
            }

            End(self);
            return new(lowered);
        }

        SilValue? CoreMethod(string name, SilValue self, List<SilValue> arguments, BoundCall call) {
            SilValue Builtin(string builtin, SilType result, params SilValue[] operands) =>
                Emit(new BuiltinInst(builtin, result, operands));

            switch (self.Type, name, arguments.Count) {
                case (_, "toString", 0) when SilBuiltins.Find($"string_from_{self.Type}") is not null:
                    return Builtin($"string_from_{self.Type}", SilType.String, self);

                case (_, "abs", 0) when self.Type == SilType.Int || self.Type == SilType.F64:
                    return Builtin($"abs_{self.Type}", self.Type, self);

                case (_, "min" or "max", 1) when self.Type == SilType.Int || self.Type == SilType.F64:
                    return Builtin($"{name}_{self.Type}", self.Type, self, arguments[0]);

                case (_, "at", 1) when self.Type == SilType.String:
                    return Builtin("string_at", SilType.Char, self, arguments[0]);

                case (_, "slice", 2) when self.Type == SilType.String:
                    return Builtin("string_slice", SilType.String, self, arguments[0], arguments[1]);

                case (_, "appending", 1) when self.Type == SilType.String:
                    return Builtin("string_concat", SilType.String, self, arguments[0]);

                case (_, "contains", 1) when self.Type == SilType.String:
                    return Builtin("string_contains", SilType.Bool, self, arguments[0]);

                case (_, "trimmed", 0) when self.Type == SilType.String:
                    return Builtin("string_trimmed", SilType.String, self);

                case (_, "split", 1) when self.Type == SilType.String:
                    return Builtin("string_split", new ArrayType(SilType.String), self, arguments[0]);

                case (_, "indexOf", 1) when self.Type == SilType.String: {
                    var index = Builtin("string_index_of", SilType.Int, self, arguments[0]);
                    var found = Builtin("cmp_ge_Int", SilType.Bool, index, Emit(new IntegerLiteralInst(SilType.Int, 0)));
                    return MakeOptional(call.Type, found, index, call);
                }

                case (_, "toInt" or "toF64", 0) when self.Type == SilType.String: {
                    var target = name == "toInt" ? SilType.Int : SilType.F64;
                    var parsed = Builtin($"string_to_{target}", SilType.Tuple([SilType.Bool, target]), self);
                    return MakeOptional(call.Type, Emit(new TupleExtractInst(parsed, 0)), Emit(new TupleExtractInst(parsed, 1)), call);
                }

                default:
                    return Missing(call, $"the member '{name}' of '{call.Receiver?.Type.ToDisplayString()}'");
            }
        }

        /// <summary>An optional holding the value when the flag is set, and none otherwise.</summary>
        SilValue? MakeOptional(TypeSymbol type, SilValue present, SilValue value, BoundNode at) {
            if (LowerType(type) is not EnumType optional) {
                return Missing(at, $"an optional of type '{type.ToDisplayString()}'");
            }

            var some = function.AddBlock();
            var none = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(optional);
            Jump(new CondBranchInst(present, some, [], none, []));
            _current = some;
            Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("Some"), value)));
            _current = none;
            Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("None"), null)));
            _current = after;
            return result;
        }

        /// <summary>The computed properties the core gives its own types.</summary>
        SilValue? ComputedProperty(BoundFieldAccess access, BoundExpression receiver) {
            var name = access.Field.Name;
            string? builtin = (Sub(receiver.Type), name) switch {
                (ArrayTypeSymbol, "count" or "isEmpty") => "array_count",
                (var t, "count" or "isEmpty") when t.Equals(Core.String) => "string_count",
                (_, "isDigit") when Sub(receiver.Type).Equals(Core.Char) => "char_is_digit",
                (_, "isLetter") when Sub(receiver.Type).Equals(Core.Char) => "char_is_letter",
                (_, "isWhitespace") when Sub(receiver.Type).Equals(Core.Char) => "char_is_whitespace",
                _ => null,
            };

            if (builtin is null) {
                return Missing(access, $"the property '{name}' of '{receiver.Type.ToDisplayString()}'");
            }

            if (Borrow(receiver) is not { } self) {
                return null;
            }

            var resultType = builtin.EndsWith("count", StringComparison.Ordinal) ? SilType.Int : SilType.Bool;
            SilValue result = Emit(new BuiltinInst(builtin, resultType, self));
            End(self);

            if (name == "isEmpty") {
                result = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, result, Emit(new IntegerLiteralInst(SilType.Int, 0))));
            }

            return result;
        }

        static string Describe(SyntaxNode syntax) {
            var text = syntax.ToString().Trim();
            var line = text.Split('\n')[0];
            return line.Length > 40 ? line[..40] + "…" : line;
        }
    }
}
