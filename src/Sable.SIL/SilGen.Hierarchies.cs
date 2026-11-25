using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Generic classes in a hierarchy, compiled once (#297 stage 4): one with
///     a superclass, or that a subclass may override. A method a generic
///     class declares takes its slot in the vtable in the convention it is
///     compiled once in — its class's type parameters opaque, their metadata
///     and tables after its own parameters — as Swift gives a vtable's entry
///     the abstraction pattern of the declaration that introduces it. What
///     overrides it, with other type parameters or none, is in the slot as a
///     thunk that converts (<see cref="SlotThunk" />), so that a call through
///     the slot is the same whatever class the object is of, and the class
///     made at run time for arguments known only there has the one vtable.
/// </summary>
/// <remarks>
///     In an unoptimised build that compiles generics once; an optimised one
///     keeps a hierarchy's methods in their instances' types, as it keeps the
///     functions a generic type stores, for devirtualisation to see through.
/// </remarks>
public sealed partial class SilGen {
    internal static bool HierarchiesOnce => Generics == GenericsMode.Unspecialised && !AmbientOptimised.Value;

    /// <summary>Whether what a class inherits, if anything, can be laid out at run time too.</summary>
    static bool HasPlainSuperclass(NamedTypeSymbol definition) {
        if (SuperclassOf(definition) is not { } superclass) {
            return true;
        }

        return HierarchiesOnce && !superclass.OriginalDefinition.IsNoncopyable
               && (superclass.IsConstructed
                   ? IsPlainGenericClass(superclass.OriginalDefinition)
                     && superclass.TypeArguments.All(a => !Mentions(a, definition.TypeParameters) || IsHeld(a, definition.TypeParameters) && a is not MetatypeTypeSymbol)
                   : superclass.OriginalDefinition is { TypeParameters.IsEmpty: true } && HasPlainSuperclass(superclass));
    }

    /// <summary>The class a type is, or one it inherits from, declared as <paramref name="owner" />: with its type arguments there.</summary>
    static NamedTypeSymbol? InstanceOf(NamedTypeSymbol type, NamedTypeSymbol owner) {
        for (var at = type; at is not null;) {
            if (ReferenceEquals(at.OriginalDefinition, owner.OriginalDefinition)) {
                return at;
            }

            at = SuperclassOf(at.OriginalDefinition) is { } superclass ? TypeSubstitution.For(at).Apply(superclass) as NamedTypeSymbol : null;
        }

        return null;
    }

    readonly Dictionary<FunctionSymbol, FunctionSymbol> _slotRoots = new(ReferenceEqualityComparer.Instance);

    /// <summary>The method, or the accessor, whose declaration introduces the slot this one takes: itself, unless it overrides another.</summary>
    internal FunctionSymbol SlotRoot(FunctionSymbol method) {
        if (_slotRoots.TryGetValue(method, out var known)) {
            return known;
        }

        var root = method.RootOverridden ?? method;
        if (method is { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol property } && Owner(method) is { } owner) {
            for (var at = SuperclassOf(owner.OriginalDefinition); at is not null; at = SuperclassOf(at.OriginalDefinition)) {
                if (at.OriginalDefinition.GetMembers(property.Name).OfType<FieldSymbol>().FirstOrDefault(static f => f is { IsStatic: false, IsComputed: true }) is { } inherited
                    && (method.Name == "get" ? GetterOf(inherited) : SetterOf(inherited)) is { } accessor) {
                    root = accessor;
                }
            }
        }

        return _slotRoots[method] = root;
    }

    /// <summary>
    ///     What a dynamic call of a method is a call of, where its slot is not
    ///     in the convention the method itself has: the method that introduces
    ///     the slot, when either is a generic class's. Null for any other.
    /// </summary>
    internal FunctionSymbol? DynamicRoot(FunctionSymbol method) =>
        HierarchiesOnce && SlotRoot(method) is var root && !ReferenceEquals(root, method) && (IsGeneric(root) || IsGeneric(method)) ? root : null;

    /// <summary>Whether a slot's entries are in the convention its generic class's method is compiled once in.</summary>
    bool IsSlotOnce(FunctionSymbol root) => HierarchiesOnce && IsGeneric(root) && root.TypeParameters.IsEmpty && UnspecialisedShape(root) is not null;

    /// <summary>
    ///     Whether an object of a generic class can be made where its type
    ///     arguments are known only at run time: every slot of its vtable is
    ///     one that any of its instances fills alike.
    /// </summary>
    bool HasSlotsOnce(NamedTypeSymbol definition) {
        for (var at = definition; at is not null; at = SuperclassOf(at)?.OriginalDefinition) {
            if (!IsInherited(at) && SuperclassOf(at) is null && ReferenceEquals(at, definition)) {
                return true;
            }

            foreach (var method in VirtualMembers(at)) {
                if (!method.TypeParameters.IsEmpty) {
                    continue;
                }

                if (IsGeneric(method) && UnspecialisedShape(method) is null || SlotRoot(method) is var root && IsGeneric(root) && !IsSlotOnce(root)) {
                    return false;
                }
            }
        }

        return true;
    }

    IEnumerable<FunctionSymbol> VirtualMembers(NamedTypeSymbol definition) =>
        definition.Members.OfType<FunctionSymbol>()
            .Concat(definition.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: true }).SelectMany(f => new[] { GetterOf(f), SetterOf(f) }).OfType<FunctionSymbol>())
            .Where(m => IsVirtual(m) && _names.ContainsKey(m));

    /// <summary>
    ///     The entry a class has for a method of its own in a build whose
    ///     generic slots are compiled once: the method itself, where it is
    ///     what introduces the slot, or a thunk in the slot's convention.
    ///     Null for a slot filled as any other, with the method's instance.
    /// </summary>
    string? SlotEntry(ClassType type, NamedTypeSymbol symbol, FunctionSymbol method) {
        if (!HierarchiesOnce || !method.TypeParameters.IsEmpty || method.DeclaringSyntax.FirstOrDefault() is not { } at) {
            return null;
        }

        var root = SlotRoot(method);
        var opaque = SilType.MentionsOpaque(type);
        if (!IsSlotOnce(root) && !opaque) {
            return null;
        }

        FunctionType slot;
        if (IsGeneric(root)) {
            if (!IsSlotOnce(root) || UnspecialisedInstance(root, UnspecialisedShape(root)!, at) is not var (name, rootType)) {
                return null;
            }

            if (ReferenceEquals(root, method)) {
                return name;
            }

            slot = rootType;
        } else if (_names.TryGetValue(root, out var rootName) && (_module.Find(rootName)?.Type ?? SignatureOf(root, at, TypeSubstitution.Empty)) is { } declared) {
            if (ReferenceEquals(root, method)) {
                return rootName;
            }

            slot = declared;
        } else {
            return null;
        }

        // What it converts to: the method compiled once, or the one for this class.
        var rootShape = IsGeneric(root) ? UnspecialisedShape(root)! : null;
        if (!IsGeneric(method)) {
            return _names.TryGetValue(method, out var own) && (_module.Find(own)?.Type ?? SignatureOf(method, at, TypeSubstitution.Empty)) is { } plain
                ? SlotThunk(own, plain, slot, rootShape, null, at)
                : null;
        }

        if (UnspecialisedShape(method) is { } shape && UnspecialisedInstance(method, shape, at) is var (once, onceType)) {
            return SlotThunk(once, onceType, slot, rootShape, shape, at);
        }

        return !opaque && Instance(method, TypeSubstitution.For(symbol), at) is var (instance, instanceType) ? SlotThunk(instance, instanceType, slot, rootShape, null, at) : null;
    }

    /// <summary>
    ///     A method in the convention of the slot it takes: given what a call
    ///     through the slot passes, it converts each argument, the result and
    ///     the error as a reabstraction thunk does, with the object as the
    ///     class the method is of, and gives a method compiled once the
    ///     metadata of its own class's type arguments, read from the object's.
    /// </summary>
    string? SlotThunk(string method, FunctionType source, FunctionType slot, GenericShape? rootShape, GenericShape? shape, SyntaxNode at) {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{method} in {slot}")))[..16];
        var name = $"$slot${hash}";
        if (_module.Find(name) is not null) {
            return name;
        }

        if (shape is not null) {
            foreach (var (_, each) in shape.Requirements) {
                OpenedProtocol(each, false, false, at);
            }
        }

        var hiddenTarget = rootShape is null ? 0 : rootShape.Parameters.Length + rootShape.Requirements.Length;
        var hiddenSource = shape is null ? 0 : shape.Parameters.Length + shape.Requirements.Length;
        FunctionType Own(FunctionType of, int hidden) => new([..of.Parameters.Skip(1).SkipLast(hidden)], of.Result, of.Thrown, isAsync: of.IsAsync);
        return MakeConversion(_module, Own(source, hiddenSource), Own(slot, hiddenTarget), name, slot, hiddenTarget, (entry, self) => {
            // Through its own class's instance of the superclass the slot is of, then down to it.
            var wanted = (ClassType)source.Parameters[0];
            SilValue seen = self;
            if (self.Type != wanted) {
                var ancestor = wanted;
                while (ancestor is not null && ancestor != self.Type && !SilType.SameErasingOpaque(ancestor, self.Type, opaqueOnly: true)) {
                    ancestor = ancestor.Superclass;
                }

                if (ancestor is null) {
                    return null;
                }

                if (ancestor != seen.Type) {
                    seen = entry.Append(new BuiltinInst("opaque_borrow_as", ancestor, seen));
                }

                if (ancestor != wanted) {
                    seen = entry.Append(new UncheckedRefCastInst(seen, wanted));
                }
            }

            var hidden = new List<SilValue>();
            if (shape is not null && Owner(_unspecialised[method].Symbol) is { } owner) {
                var metadata = entry.Append(new BuiltinInst($"object_metadata_as:{QualifiedName(owner)}", SilType.Metadata, seen));
                hidden = TemplateArguments(entry, metadata, shape);
            }

            return (entry.Append(new FunctionRefInst(method, source)), [seen], hidden);
        });
    }

    /// <summary>
    ///     The function that gives the metadata of the superclass a generic
    ///     class has from the metadata of an instance of it made at run time:
    ///     a template's accessor, which reads the type arguments there. Null
    ///     for a class with none.
    /// </summary>
    void SuperclassAccessor(NamedTypeSymbol definition) {
        if (SuperclassOf(definition) is not { } superclass || _module.SuperclassAccessors.ContainsKey(QualifiedName(definition))) {
            return;
        }

        var stem = $"$template$superclass${Sanitise(QualifiedName(definition))}";
        _module.SuperclassAccessors[QualifiedName(definition)] = stem;
        var shape = new GenericShape(definition.TypeParameters, []);
        using (Opaque(shape))
        using (AssociatedContext(definition)) {
            if (Lower(superclass) is not { } lowered) {
                _module.SuperclassAccessors.Remove(QualifiedName(definition));
                return;
            }

            var body = new SilFunction(stem + "$body", new FunctionType([..shape.Parameters.Select(static _ => SilType.Metadata)], SilType.Metadata));
            body.ParameterNames.AddRange(shape.Parameters.Select(static p => $"$metadata.{ParameterName(p)}"));
            var block = body.AddBlock();
            foreach (var parameter in body.Type.Parameters) {
                block.AddArgument(parameter);
            }

            block.Append(new ReturnInst(block.Append(new MetadataInst(lowered))));
            AssociatedRecipes(body, definition, shape);
            _module.Add(body);
            var accessor = new SilFunction(stem, new FunctionType([SilType.Metadata], SilType.Metadata));
            accessor.ParameterNames.Add("$metadata.Self");
            var entry = accessor.AddBlock();
            var arguments = TemplateArguments(entry, entry.AddArgument(SilType.Metadata), shape);
            entry.Append(new ReturnInst(entry.Append(new ApplyInst(entry.Append(new FunctionRefInst(body.Name, body.Type)), SilType.Metadata, [..arguments]))));
            _module.Add(accessor);
        }
    }

    /// <summary>
    ///     A method called through its slot, as a function of the type it has
    ///     where the receiver's type is known: what a bound method value calls
    ///     when the slot's convention is another.
    /// </summary>
    string DispatchShim(FunctionSymbol method, string name, FunctionType type, TypeSubstitution substitution) {
        var shim = $"{name}$dispatch";
        if (_module.Find(shim) is null && method.DeclaringSyntax.FirstOrDefault() is { } syntax && SourceOf(syntax) is { } source) {
            var function = new SilFunction(shim, type) { SourceFile = source.FileName };
            var call = new BoundCall(syntax, new BoundSelfAccess(syntax, SelfType(method, substitution), false), method,
                [..method.Parameters.Select(p => (BoundExpression)new BoundParameterAccess(syntax, p))], method.ReturnType) { Substitution = substitution };
            BoundStatement[] statements = method.ReturnType.Equals(Core.Void)
                ? [new BoundExpressionStatement(syntax, call), new BoundReturn(syntax, null)]
                : [new BoundReturn(syntax, call)];
            new FunctionLowering(this, method, function, source, substitution).Lower(new BoundBlock(syntax, [], [..statements]));
            function.RemoveUnreachableBlocks();
            _module.Add(function);
        }

        return shim;
    }
}
