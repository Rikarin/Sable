using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Generators (spec/SYNTAX.md §5, "Generators"): the coroutine split.
/// </summary>
/// <remarks>
///     <para>
///         A generator's call builds its sequence, a struct of what it captured
///         — <c>self</c> and its arguments, by value — and runs nothing; the
///         sequence's <c>makeIterator()</c> builds the iterator: the captured
///         values again, a resume state of 0, and a field for every slot the
///         body uses, each empty. The iterator's <c>next()</c> runs the body
///         from where the state says it stopped until it yields or ends.
///     </para>
///     <para>
///         The body is lowered once, as one function — its <i>resume</i>
///         function — taking the iterator's address and the captured values,
///         as an ordinary function takes its parameters. Every slot it makes
///         (every local, every loop's counter, the array a loop goes through)
///         is in the iterator instead of the frame: the slot's address is
///         that field's, so a value the body leaves in a slot is there when
///         <c>next()</c> is called again. A <c>yield</c> stores the state to
///         resume at and returns the element; the entry branches on the state
///         to where the body left off, to its start for 0, and to an end that
///         returns none once the body has finished. Nothing else may live
///         across a yield, and lowering checks that nothing does.
///     </para>
///     <para>
///         Storage that holds nothing is empty, all zeros (a released slot is
///         emptied, as a taken one is), so the fields of slots the body is not
///         using are empty, and copying or destroying the iterator — dropping it
///         part-way — copies or destroys exactly what the body holds. The
///         iterator is an ordinary struct: no allocation, and a copy goes on
///         from where the original was, separately.
///     </para>
/// </remarks>
public sealed partial class SilGen {
    /// <summary>The state an iterator is in once its body has finished.</summary>
    const long GeneratorDone = -1;

    /// <summary>
    ///     A closure that is a generator, where it is lowered: its body, its
    ///     source, and its context, which the sequence and the iterator hold
    ///     after its arguments. By the generator and its type arguments there.
    /// <summary>
    ///     A closure that is a generator, where it is lowered: its body, its
    ///     source, and its context, which the sequence and the iterator hold
    ///     after its arguments. By the generator and its type arguments there.
    /// </summary>
    readonly Dictionary<string, (BoundBlock Body, SourceText Source, SyntaxNode Syntax, ClosureInfo Closure)> _generatorClosures = new(StringComparer.Ordinal);

    static string ClosureKey(GeneratorSymbols generator, NamedTypeSymbol construction) =>
        $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(generator.Function)}<{string.Join(",", construction.TypeArguments.Select(static a => a.ToDisplayString()))}>";

    /// <summary>
    ///     Where an iterator keeps its body's state: the captured values, the
    ///     resume state and the slots. Usually the iterator struct itself; for
    ///     a body with a <c>defer</c>, an object the iterator holds, whose
    ///     deinit resumes the body to leave it — running the defers still
    ///     pending — when the last iterator holding it goes.
    /// </summary>
    /// <param name="Storage">The iterator struct, or the frame object.</param>
    /// <param name="Captured">How many captured values come first.</param>
    /// <param name="Deinit">For a frame object, its deinit.</param>
    sealed record GeneratorFrame(SilFunction Resume, SourceText Source, int Captured, NominalType Storage, SilFunction? Deinit) {
        public bool Boxed => Storage is ClassType;
    }

    /// <summary>For each iterator's SIL type, where its state is and the functions that run its body.</summary>
    readonly Dictionary<StructType, GeneratorFrame> _resumes = new(ReferenceEqualityComparer.Instance);

    /// <summary>The address of a field of a generator's state: in the struct it points to, or in the frame object.</summary>
    static SilInstruction FrameField(SilValue frame, int field) =>
        frame.Type is ClassType ? new RefElementAddrInst(frame, field) : new StructElementAddrInst(frame, field);

    /// <summary>True for a body whose defers must run when its iterator is dropped part-way.</summary>
    static bool Defers(BoundNode node) =>
        node is BoundDefer || node is not BoundClosure && node.Children.Any(Defers);

    /// <summary>
    ///     The sequence or iterator type of a generator: the captured values
    ///     and, for the iterator, the state and the body's slots — which the
    ///     body, lowered here, decides.
    /// </summary>
    StructType? LowerGeneratorType(NamedTypeSymbol named, GeneratorSymbols generator) {
        var substitution = TypeSubstitution.For(named);
        var structType = Begin(named, new StructType(TypeName(named)));
        if (CapturedOf(generator, named, substitution) is not { } captured) {
            return null;
        }

        if (ReferenceEquals(named.OriginalDefinition, generator.SequenceType)) {
            structType.Fields.AddRange(captured);
            return HoldsItself(structType) ? null : structType;
        }

        if (GeneratorBody(generator, named) is not { } body) {
            return null;
        }

        NominalType storage = structType;
        if (Defers(body.Body)) {
            var frame = _module.AddType(new ClassType(structType.Name + "$frame") { EmptyFrom = captured.Count + 1 });
            frame.Fields.AddRange(captured);
            frame.Fields.Add(("$state", SilType.Int));
            structType.Fields.Add(("$frame", frame));
            storage = frame;
        } else {
            structType.Fields.AddRange(captured);
            structType.Fields.Add(("$state", SilType.Int));
        }

        if (!LowerResume(generator, named, structType, storage, captured.Count, substitution)) {
            return null;
        }

        if (HoldsItself(structType)) {
            if (generator.Function.DeclaringSyntax.FirstOrDefault() is { } at) {
                Unsupported<object>(at, $"a generator whose body holds its own iterator — it goes through '{generator.Function.Name}' itself — which would make the iterator hold itself");
            }

            return null;
        }

        return structType;
    }

    /// <summary>What a generator's call captures, in order: <c>self</c>, then each argument.</summary>
    List<(string Name, SilType Type)>? CapturedOf(GeneratorSymbols generator, NamedTypeSymbol construction, TypeSubstitution substitution) {
        var function = generator.Function;
        var at = function.DeclaringSyntax.FirstOrDefault();
        var captured = new List<(string, SilType)>();
        if (HasSelf(function)) {
            if (Lower(SelfType(function, substitution)) is not { } self) {
                return null;
            }

            captured.Add(("self", self));
        }

        foreach (var parameter in function.Parameters) {
            if (parameter.Type is OpaqueTypeSymbol { Origin: null }) {
                if (at is not null) {
                    Unsupported<object>(at, $"a generator with a 'some' parameter ('{parameter.Name}'); write it as a generic parameter");
                }

                return null;
            }

            if (Lower(Substitute(substitution, parameter.Type)) is not { } type) {
                return null;
            }

            captured.Add((parameter.Name, type));
        }

        // A closure's context, last, as its code takes it.
        if (_generatorClosures.TryGetValue(ClosureKey(generator, construction), out var closure) && closure.Closure.Context is { } context) {
            captured.Add(("$context", context));
        }

        return captured;
    }

    /// <summary>The bound body of a generator, and the source it is in.</summary>
    (BoundBlock Body, SyntaxNode Syntax, SourceText Source)? GeneratorBody(GeneratorSymbols generator, NamedTypeSymbol construction) {
        if (_generatorClosures.TryGetValue(ClosureKey(generator, construction), out var closure)) {
            return (closure.Body, closure.Syntax, closure.Source);
        }

        if (!_declarations.TryGetValue(generator.Function, out var declaration)
            || declaration.Model.GetBoundNode(declaration.Syntax) is not BoundBlock body) {
            return null;
        }

        return (body, declaration.Syntax, declaration.Model.SyntaxTree.Text);
    }

    /// <summary>Lowers a generator's body as the resume function of an iterator type, whose slots it adds to where its state is.</summary>
    bool LowerResume(GeneratorSymbols generator, NamedTypeSymbol construction, StructType iterator, NominalType storage, int captured, TypeSubstitution substitution) {
        if (GeneratorBody(generator, construction) is not var (body, syntax, source)) {
            return false;
        }

        var function = generator.Function;
        if (Lower(Substitute(substitution, new OptionalTypeSymbol(generator.Element))) is not EnumType result) {
            return false;
        }

        SilType? thrown = null;
        if (generator.Next.ThrownType is { } thrownSymbol && !Substitute(substitution, thrownSymbol).Equals(Core.Never)
            && (thrown = Lower(Substitute(substitution, thrownSymbol))) is null) {
            return false;
        }

        var fields = storage is ClassType frame ? frame.Fields : ((StructType)storage).Fields;
        var boxed = storage is ClassType;

        // The frame — the iterator's address, or the object — and, for an
        // object, whether to leave the body rather than go on; then what the
        // body takes as a function would.
        ImmutableArray<SilType> parameters = [
            boxed ? storage : SilType.Address(storage),
            ..boxed ? [SilType.Bool] : Array.Empty<SilType>(),
            ..fields.Take(captured).Select(static f => f.Type),
        ];
        var resume = new SilFunction(iterator.Name + "$resume", new FunctionType(parameters, result, thrown)) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
        resume.ParameterNames.Add("$frame");
        if (boxed) {
            resume.ParameterNames.Add("$leave");
        }

        resume.ParameterNames.AddRange(fields.Take(captured).Select(static f => f.Name));

        var reported = _diagnostics.Count;
        var lowering = _loweringInstance;
        try {
            var state = new GeneratorState(storage, captured, result);
            var closure = _generatorClosures.TryGetValue(ClosureKey(generator, construction), out var made) && made.Closure.Context is not null ? made.Closure : null;
            new FunctionLowering(this, function, resume, source, WithAssociatedTypes(function, substitution), closure) { Generator = state }.Lower(body);
            resume.RemoveUnreachableBlocks();
        } finally {
            _loweringInstance = lowering;
            if (TreeOf(source) is { } tree) {
                Place(reported, tree);
            }
        }

        SilFunction? deinit = null;
        if (storage is ClassType box) {
            // The frame's deinit: the body left from where it stopped.
            deinit = new SilFunction(box.Name + "$deinit", new FunctionType([box], SilType.Unit)) { SourceFile = resume.SourceFile, Line = resume.Line };
            deinit.ParameterNames.Add("self");
            var entry = deinit.AddBlock();
            var self = entry.AddArgument(box);
            var leave = entry.Append(new IntegerLiteralInst(SilType.Bool, 1));
            var (values, copies) = CapturedValues(entry, self, box.Fields, captured);
            var callee = entry.Append(new FunctionRefInst(resume.Name, resume.Type));
            var done = entry;
            SilValue none;
            if (thrown is null) {
                none = entry.Append(new ApplyInst(callee, result, [self, leave, ..values]));
            } else {
                // Leaving runs only defers and returns, so it never throws;
                // the resume function's type says it may.
                done = deinit.AddBlock();
                none = done.AddArgument(result);
                var failed = deinit.AddBlock();
                var error = failed.AddArgument(thrown);
                entry.Append(new TryApplyInst(callee, result, [self, leave, ..values], done, failed));
                if (!thrown.IsTrivial) {
                    failed.Append(new DestroyValueInst(error));
                }

                failed.Append(new UnreachableInst());
            }

            if (!result.IsTrivial) {
                done.Append(new DestroyValueInst(none));
            }
            Destroy(done, copies);
            done.Append(new ReturnInst(null));
            box.Deinit = deinit.Name;
        }

        _resumes[iterator] = new(resume, source, captured, storage, deinit);
        return true;
    }

    /// <summary>The captured values, read from where a generator's state is: copies, since the body writes there.</summary>
    static (List<SilValue> Values, List<SilValue> Copies) CapturedValues(SilBlock block, SilValue frame, List<(string Name, SilType Type)> fields, int captured) {
        var values = new List<SilValue>();
        var copies = new List<SilValue>();
        for (var i = 0; i < captured; i++) {
            var field = block.Append(FrameField(frame, i));
            if (fields[i].Type.IsTrivial) {
                values.Add(block.Append(new LoadInst(field, LoadKind.Trivial)));
            } else {
                var value = block.Append(new LoadInst(field, LoadKind.Copy));
                copies.Add(value);
                values.Add(value);
            }
        }

        return (values, copies);
    }

    static void Destroy(SilBlock block, List<SilValue> values) {
        for (var i = values.Count - 1; i >= 0; i--) {
            block.Append(new DestroyValueInst(values[i]));
        }
    }

    /// <summary>The functions that run an iterator's body, in this module once any of its parts is.</summary>
    void EmitFrame(GeneratorFrame frame) {
        foreach (var function in (SilFunction?[])[frame.Resume, frame.Deinit]) {
            if (function is null || _module.Find(function.Name) is not null) {
                continue;
            }

            _module.Add(function);
            MoveChecker.Check(function, frame.Source, _diagnostics);
            _sources[function] = frame.Source;
        }
    }

    /// <summary>
    ///     The code of a generator's parts, which no source spells: its call,
    ///     which captures; <c>makeIterator()</c>, which starts an iterator; and
    ///     <c>next()</c>, which resumes the body. False for any other function.
    /// </summary>
    bool LowerGeneratorPart(FunctionSymbol symbol, SyntaxNode syntax, SourceText source, string name, FunctionType type, TypeSubstitution substitution) {
        if (_compilation.GetGenerator(symbol) is not { } generator) {
            return false;
        }

        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
        var entry = function.AddBlock();
        var arguments = type.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();

        if (ReferenceEquals(symbol, generator.Function)) {
            // The call: what it captures, copied, in the sequence — or, for
            // an iterator generator, in an iterator at its start.
            if (HasSelf(symbol)) {
                function.ParameterNames.Add("self");
            }

            function.ParameterNames.AddRange(symbol.Parameters.Select(static p => p.Name));
            var captured = arguments.Select((a, i) => type.IsOwned(i) || a.Type.IsTrivial ? a : entry.Append(new CopyValueInst(a))).ToList();
            entry.Append(new ReturnInst(Make(entry, (StructType)type.Result, captured)));
        } else if (ReferenceEquals(symbol, generator.MakeIterator)) {
            // 'makeIterator()': the sequence's captured values, copied, in an iterator at its start.
            function.ParameterNames.Add("self");
            var sequence = arguments[0];
            var fields = ((StructType)sequence.Type).Fields;
            var captured = fields.Select((f, i) => {
                var field = entry.Append(new StructExtractInst(sequence, i));
                return f.Type.IsTrivial ? (SilValue)field : entry.Append(new CopyValueInst(field));
            }).ToList();
            entry.Append(new ReturnInst(Make(entry, (StructType)type.Result, captured)));
        } else {
            // 'next()': the body, resumed, given what the iterator captured.
            function.ParameterNames.Add("self");
            var iterator = (StructType)((AddressType)type.Parameters[0]).Pointee;
            if (!_resumes.TryGetValue(iterator, out var frame)) {
                return true;
            }

            EmitFrame(frame);
            var self = arguments[0];
            var held = new List<SilValue>();
            SilValue state = self;
            var leading = new List<SilValue>();
            if (frame.Boxed) {
                state = entry.Append(new LoadInst(entry.Append(new StructElementAddrInst(self, 0)), LoadKind.Copy));
                held.Add(state);
                leading.Add(entry.Append(new IntegerLiteralInst(SilType.Bool, 0)));
            }

            var (values, copies) = CapturedValues(entry, state, frame.Boxed ? ((ClassType)frame.Storage).Fields : iterator.Fields, frame.Captured);
            held.InsertRange(0, copies);
            var callee = entry.Append(new FunctionRefInst(frame.Resume.Name, frame.Resume.Type));
            SilValue[] passed = [state, ..leading, ..values];
            if (frame.Resume.Type.Thrown is null) {
                var element = entry.Append(new ApplyInst(callee, type.Result, passed));
                Destroy(entry, held);
                entry.Append(new ReturnInst(element));
            } else {
                var normal = function.AddBlock();
                var failed = function.AddBlock();
                var element = normal.AddArgument(type.Result);
                var error = failed.AddArgument(frame.Resume.Type.Thrown);
                entry.Append(new TryApplyInst(callee, type.Result, passed, normal, failed));
                Destroy(normal, held);
                normal.Append(new ReturnInst(element));
                Destroy(failed, held);
                failed.Append(new ThrowInst(error));
            }
        }

        _module.Add(function);
        _sources[function] = source;
        _ = substitution;
        return true;
    }

    /// <summary>
    ///     What a generator's call or <c>makeIterator()</c> gives: the
    ///     sequence of the captured values, or an iterator at its start.
    /// </summary>
    SilValue Make(SilBlock block, StructType made, List<SilValue> captured) {
        if (!_resumes.TryGetValue(made, out var frame)) {
            return block.Append(new StructInst(made, [..captured]));
        }

        EmitFrame(frame);
        return Start(block, made, frame, captured);
    }

    /// <summary>
    ///     An iterator at its start: the captured values, state 0, and every
    ///     slot's field empty — built in storage that starts empty, as a
    ///     struct's initialiser builds its value, and taken out; or, for a
    ///     frame object, in a new one, which starts empty too.
    /// </summary>
    static SilValue Start(SilBlock block, StructType iterator, GeneratorFrame frame, List<SilValue> captured) {
        if (frame.Storage is ClassType box) {
            var made = block.Append(new AllocRefInst(box));
            for (var i = 0; i < captured.Count; i++) {
                block.Append(new StoreInst(captured[i], block.Append(new RefElementAddrInst(made, i)), StoreKind.Init));
            }

            block.Append(new StoreInst(block.Append(new IntegerLiteralInst(SilType.Int, 0)), block.Append(new RefElementAddrInst(made, captured.Count)), StoreKind.Init));
            return block.Append(new StructInst(iterator, made));
        }

        var storage = block.Append(new AllocStackInst(iterator));
        for (var i = 0; i < captured.Count; i++) {
            block.Append(new StoreInst(captured[i], block.Append(new StructElementAddrInst(storage, i)), StoreKind.Init));
        }

        var state = block.Append(new StructElementAddrInst(storage, captured.Count));
        block.Append(new StoreInst(block.Append(new IntegerLiteralInst(SilType.Int, 0)), state, StoreKind.Init));
        var started = block.Append(new LoadInst(storage, iterator.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
        block.Append(new DeallocStackInst(storage));
        return started;
    }

    /// <summary>What lowering a generator's body as its resume function needs to know.</summary>
    /// <param name="Storage">Where its state is — the iterator, or its frame object — whose fields the slots become.</param>
    /// <param name="State">The index of its resume state.</param>
    /// <param name="Result">What the body gives each time: an element, or none at the end.</param>
    internal sealed record GeneratorState(NominalType Storage, int State, EnumType Result) {
        /// <summary>Each yield's place to resume at, by its state: 1 for the first.</summary>
        public List<SilBlock> Resumes { get; } = [];

        /// <summary>For a frame object, each yield's way out: the body left from there, its defers run.</summary>
        public List<SilBlock> Leaves { get; } = [];

        public bool Boxed => Storage is ClassType;

        public List<(string Name, SilType Type)> Fields => Storage is ClassType box ? box.Fields : ((StructType)Storage).Fields;
    }

    sealed partial class FunctionLowering {
        /// <summary>
        ///     A closure that yields: its code captures its arguments and its
        ///     context in its sequence (or iterator), whose parts are lowered
        ///     as a generator function's are — from the closure's body.
        /// </summary>
        SilValue? GeneratorClosure(BoundClosure closureExpression, GeneratorSymbols yielding) {
            var captures = generator.CapturesOf(closureExpression);
            var name = generator.ClosureName(function.Name);
            ClassType? contextType = null;
            if (captures.Count > 0 && (contextType = ContextType(closureExpression, captures, name)) is null) {
                return null;
            }

            if (Sub(yielding.Result) is not NamedTypeSymbol construction) {
                return Missing(closureExpression, "a generator closure");
            }

            var info = new ClosureInfo(contextType, captures.ToDictionary(static c => c.Key, static c => c));
            generator._generatorClosures[ClosureKey(yielding, construction)] = (closureExpression.Body, source, closureExpression.Syntax, info);

            // Its parts are found as a declared generator's are.
            var model = generator._compilation.GetSemanticModel(generator.TreeOf(source)!);
            foreach (var part in (FunctionSymbol?[])[yielding.MakeIterator, yielding.Next]) {
                if (part is not null && generator._names.TryAdd(part, $"{name}${part.Name}")) {
                    generator._declarations[part] = (closureExpression.Syntax, model);
                }
            }

            if (LowerType(closureExpression.Type) is not ThickFunctionType thick) {
                return Missing(closureExpression, $"a closure of type '{closureExpression.Type.ToDisplayString()}'");
            }

            // Not generic: lowered once, here.
            if (yielding.TypeParameters.IsEmpty) {
                foreach (var part in (FunctionSymbol?[])[yielding.MakeIterator, yielding.Next]) {
                    if (part is not null && generator._module.Find(generator._names[part]) is null
                        && generator.SignatureOf(part, closureExpression.Syntax, TypeSubstitution.Empty) is { } signature) {
                        generator.LowerGeneratorPart(part, closureExpression.Syntax, source, generator._names[part], signature, TypeSubstitution.Empty);
                    }
                }
            }

            // The code: what the call captures, in the sequence.
            var code = new SilFunction(name, thick.Implementation) {
                SourceFile = function.SourceFile,
                Line = source.GetLinePosition(closureExpression.Syntax.Span.Start).Line,
            };
            code.ParameterNames.AddRange(yielding.Function.Parameters.Select(static p => p.Name));
            var entry = code.AddBlock();
            var arguments = thick.Implementation.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
            var captured = arguments.Take(arguments.Count - 1)
                .Select((a, i) => thick.Implementation.IsOwned(i) || a.Type.IsTrivial ? a : entry.Append(new CopyValueInst(a)))
                .ToList();
            if (contextType is not null) {
                captured.Add(entry.Append(new CopyValueInst(entry.Append(new UncheckedRefCastInst(arguments[^1], contextType)))));
            }

            var result = (StructType)thick.Implementation.Result;
            entry.Append(new ReturnInst(generator.Make(entry, result, captured)));
            generator._module.Add(code);
            generator._sources[code] = source;

            Locate(closureExpression.Syntax);
            var reference = Emit(new FunctionRefInst(name, thick.Implementation));
            if (contextType is null) {
                return Emit(new PartialApplyInst(reference, null, thick));
            }

            return Context(closureExpression, contextType, captures) is { } context
                ? Emit(new PartialApplyInst(reference, Unhold(context), thick))
                : null;
        }

        /// <summary>Set when this is a generator's body, lowered as its resume function.</summary>
        internal GeneratorState? Generator { get; init; }

        /// <summary>In a generator's body, where its state is: the iterator's address, or its frame object.</summary>
        SilValue? _iterator;

        /// <summary>For a frame object, true when the body is to be left from where it stopped, not gone on with.</summary>
        SilValue? _leave;

        /// <summary>The generator's result for "no more": none.</summary>
        SilValue NoMoreElements() =>
            Emit(new EnumInst(Generator!.Result, Generator.Result.CaseIndex("None"), null));

        void StoreState(long state) {
            var field = Emit(FrameField(_iterator!, Generator!.State));
            Emit(new StoreInst(Emit(new IntegerLiteralInst(SilType.Int, state)), field, StoreKind.Assign));
        }

        /// <summary>
        ///     <c>yield value</c>: the state to resume at, then the value,
        ///     returned. The body goes on from a block of its own, which the
        ///     entry branches to when that state is next resumed. With a frame
        ///     object, it may instead be left from here: a block of its own
        ///     returns, as a 'return' here would, running the defers pending.
        /// </summary>
        void Yield(BoundYield statement) {
            if (Generator is not { } generator) {
                Fail(statement.Syntax, "'yield' outside a generator");
                return;
            }

            if (Expression(statement.Value) is not { } value) {
                return;
            }

            if (_temporaries.Count > 0) {
                Fail(statement.Syntax, "'yield' while an expression is half evaluated");
                return;
            }

            var element = Emit(new EnumInst(generator.Result, generator.Result.CaseIndex("Some"), Owned(value)));
            var resume = function.AddBlock();
            generator.Resumes.Add(resume);
            StoreState(generator.Resumes.Count);
            Jump(new ReturnInst(element));

            if (generator.Boxed) {
                var leave = function.AddBlock();
                generator.Leaves.Add(leave);
                _current = leave;
                Return(null);
            }

            _current = resume;
        }

        /// <summary>
        ///     Makes the lowered body resumable: each slot becomes its field of
        ///     the iterator (or its frame object), and the entry branches on
        ///     the state.
        /// </summary>
        void Split() {
            var generator = Generator!;
            var entry = _entry;

            // Each slot, the iterator's field instead.
            var fields = new Dictionary<SilValue, SilValue>(ReferenceEqualityComparer.Instance);
            foreach (var slot in _allocations) {
                var index = generator.Fields.Count;
                generator.Fields.Add(($"{slot.VariableName ?? "$slot"}${index}", slot.ElementType));
                var field = FrameField(_iterator!, index);
                field.Block = entry;
                field.Location = slot.Location;
                entry.Instructions[entry.Instructions.IndexOf(slot)] = field;
                fields[slot] = field;
            }

            foreach (var block in function.Blocks) {
                block.Instructions.RemoveAll(i => i is DeallocStackInst dealloc && fields.ContainsKey(dealloc.Address));
                foreach (var instruction in block.Instructions) {
                    for (var i = 0; i < instruction.Operands.Count; i++) {
                        if (fields.TryGetValue(instruction.Operands[i], out var field)) {
                            instruction.SetOperand(i, field);
                        }
                    }
                }
            }

            // What the entry did before the body is the body's start, as state 0.
            var start = function.AddBlock();
            var moved = entry.Instructions.Skip(_allocations.Count).ToList();
            entry.Instructions.RemoveRange(_allocations.Count, moved.Count);
            foreach (var instruction in moved) {
                // A cast of the context stays where every state can use it.
                if (instruction is UncheckedRefCastInst { Value: SilArgument { Block: var owner } } && owner == entry) {
                    entry.Append(instruction);
                    continue;
                }

                start.Append(instruction);
            }

            // The entry: on to where the state says — or, leaving, out from there.
            _current = entry;
            _location = null;
            var state = Emit(new LoadInst(Emit(FrameField(_iterator!, generator.State)), LoadKind.Trivial));
            var targets = new List<SilBlock> { start };
            targets.AddRange(generator.Resumes);
            if (_leave is not null) {
                var goOn = function.AddBlock();
                var leaving = function.AddBlock();
                Jump(new CondBranchInst(_leave, leaving, [], goOn, []));
                _current = leaving;
                Dispatch(state, generator.Leaves, first: 1);
                _current = goOn;
            }

            Dispatch(state, targets, first: 0);
            CheckNothingLivesAcrossYields(generator.Resumes);
            CheckNothingLivesAcrossYields(generator.Leaves);
        }

        /// <summary>To the block for each state from <paramref name="first" />; for any other, finished: no more, however often it is asked.</summary>
        void Dispatch(SilValue state, List<SilBlock> targets, int first) {
            for (var k = 0; k < targets.Count; k++) {
                var isState = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, state, Emit(new IntegerLiteralInst(SilType.Int, first + k))));
                var otherwise = function.AddBlock();
                Jump(new CondBranchInst(isState, targets[k], [], otherwise, []));
                _current = otherwise;
            }

            Jump(new ReturnInst(NoMoreElements()));
        }

        /// <summary>
        ///     A value the body made before a yield and uses after it would be
        ///     gone when it resumes: everything the body keeps is in its slots.
        /// </summary>
        void CheckNothingLivesAcrossYields(List<SilBlock> resumes) {
            var entry = _entry;
            var defined = new Dictionary<SilBlock, HashSet<SilValue>>();
            var used = new Dictionary<SilBlock, HashSet<SilValue>>();
            foreach (var block in function.Blocks) {
                var defs = new HashSet<SilValue>(block.Arguments, ReferenceEqualityComparer.Instance);
                var uses = new HashSet<SilValue>(ReferenceEqualityComparer.Instance);
                foreach (var instruction in block.Instructions) {
                    foreach (var operand in instruction.Operands) {
                        if (!defs.Contains(operand) && operand is SilInstruction or SilArgument) {
                            uses.Add(operand);
                        }
                    }

                    defs.Add(instruction);
                }

                defined[block] = defs;
                used[block] = uses;
            }

            var live = function.Blocks.ToDictionary(b => b, b => new HashSet<SilValue>(used[b], ReferenceEqualityComparer.Instance));
            for (var changed = true; changed;) {
                changed = false;
                foreach (var block in Enumerable.Reverse(function.Blocks)) {
                    foreach (var successor in block.Successors) {
                        foreach (var value in live[successor]) {
                            if (!defined[block].Contains(value) && live[block].Add(value)) {
                                changed = true;
                            }
                        }
                    }
                }
            }

            foreach (var resume in resumes) {
                if (live[resume].Any(v => !(v is SilArgument { Block: var b } && b == entry || v is SilInstruction { Block: var i } && i == entry))) {
                    Fail(_body!.Syntax, "a generator holding a value across 'yield' outside a variable");
                    return;
                }
            }
        }
    }
}
