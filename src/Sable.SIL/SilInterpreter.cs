using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Unicode;

namespace Sable.SIL;

/// <summary>A program stopped where the language says it must: overflow, division by zero, unreachable code.</summary>
/// <param name="message">Why, as the native program says it after <c>fatal error: </c>.</param>
/// <param name="located">
///     False for a trap with no place in the source — calls nested too
///     deeply, an object kept alive by its deinit, every task waiting — which
///     the native program reports without one too.
/// </param>
public sealed class SilTrap(string message, bool located = true) : Exception(message) {
    /// <summary>Where it happened, as <c>file:line:column</c>: the instruction that trapped, when it has a place.</summary>
    public string? Site { get; internal set; }

    internal bool Located => located;

    /// <summary>The status the program ends with: 134, as an abort is reported, or 1 for an error main threw.</summary>
    public int ExitStatus { get; internal init; } = 134;

    /// <summary>Not a trap: the program ended itself (Process.exit), and there is nothing to say.</summary>
    public bool Exits { get; internal init; }

    /// <summary>Whether the program's panic hook said it, in place of the report.</summary>
    public bool Hooked { get; internal set; }

    /// <summary>A trap in the panic hook, as it ran with this one.</summary>
    public SilTrap? HookTrap { get; internal set; }

    /// <summary>
    ///     What the native program writes to standard error as it stops: the
    ///     message, and the place on the next line — unless the panic hook
    ///     said it instead. A trap in the hook adds its own after.
    /// </summary>
    public string Report =>
        Hooked || Exits ? ""
        : HookTrap is { } inner ? Plain + $"fatal error: the panic hook trapped: {inner.Message}\n" + (inner.Site is null ? "" : $"  at {inner.Site}\n")
        : Plain;

    string Plain => Site is null ? $"fatal error: {Message}\n" : $"fatal error: {Message}\n  at {Site}\n";
}

/// <summary>
///     Runs SIL directly, so that tests can check what lowered code
///     <i>computes</i> — and what it does with memory — independently of the
///     LLVM backend.
/// </summary>
/// <remarks>
///     <para>
///         Values are <see cref="long" /> for <c>Int</c>, <see cref="bool" />
///         for <c>Bool</c>, <see cref="double" /> for <c>F64</c>, a
///         <see cref="Rune" /> for <c>Char</c>, an array of parts for a struct
///         or tuple, an <see cref="EnumValue" />, a storage reference for an
///         address, and a function's name for a function value. Integer
///         arithmetic traps on overflow and on division by zero, which is what
///         the generated code does. A step limit and a depth limit turn a
///         runaway program into a trap rather than a hung test.
///     </para>
///     <para>
///         Strings, objects and arrays are counted heap objects, as they are
///         natively. Every retain and release is carried out, so a program
///         that uses a freed object, releases one too often or leaves storage
///         holding a value as it is freed traps, and one that leaks leaves
///         <see cref="LiveObjects" /> above zero.
///     </para>
/// </remarks>
public sealed partial class SilInterpreter(SilModule module, long stepLimit = 10_000_000, int depthLimit = 2_000) {
    long _steps;

    // What runs a module needs its async functions made coroutines first.
    readonly bool _asyncLowered = LowerAsync(module);

    static bool LowerAsync(SilModule module) {
        AsyncLowering.Run(module);
        return true;
    }
    readonly Dictionary<StringLiteralInst, HeapString> _literals = [];

    /// <summary>
    ///     What one program leaves for the next of a session to go on from: the
    ///     heap and the globals. Values are the same across modules — a
    ///     function, a type, a method are found by name — so a program
    ///     compiled after another, with the same declarations, can use what
    ///     the other made.
    /// </summary>
    /// <remarks>
    ///     In a session, a global is set up the first time it is reached, by
    ///     its own initialiser (<see cref="SilGen.LowerForSession" />), and
    ///     never again: a later program finds it holding what it held.
    /// </remarks>
    public sealed class Session {
        internal HashSet<Counted> Live { get; } = [];

        /// <summary>The objects '@immortal' globals hold: never released, and what they keep alive no leak.</summary>
        internal List<(SilType Type, Counted Object)> Immortal { get; } = [];

        /// <summary>The globals' storage, made as each is first reached.</summary>
        internal Dictionary<string, Slot> Globals { get; } = [];

        /// <summary>The globals given their initial value, and those being given it.</summary>
        internal HashSet<string> Initialised { get; } = [];

        /// <summary>The globals set up so far, by name.</summary>
        public IReadOnlySet<string> GlobalNames => Initialised;

        /// <summary>
        ///     Forgets the globals not among <paramref name="kept" />: those a
        ///     program set up whose declarations are not kept, as a REPL's
        ///     failed entry's are not. A later program declaring one again
        ///     sets it up again.
        /// </summary>
        public void Forget(IReadOnlySet<string> kept) {
            foreach (var global in Initialised.Where(g => !kept.Contains(g)).ToList()) {
                Globals.Remove(global);
                Initialised.Remove(global);
            }
        }
    }

    /// <summary>The session this program goes on from, and leaves its heap and globals to; null for a program of its own.</summary>
    public Session? Carried { get; init; }

    Session? _state;
    Session State => _state ??= Carried ?? new Session();
    HashSet<Counted> _live => State.Live;
    Dictionary<string, Slot> _globals => State.Globals;

    /// <summary>Where <c>print</c> writes: one line per call, as the native runtime does.</summary>
    public StringBuilder Output { get; } = new();

    /// <summary>What <c>Runtime.writeError</c> writes, as the native runtime writes to standard error.</summary>
    public StringBuilder ErrorOutput { get; } = new();

    /// <summary>Standard input, for <c>Runtime.readLine</c>.</summary>
    public TextReader Input { get; init; } = TextReader.Null;

    /// <summary>The program's arguments, after its own name.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    ///     What the program may do outside itself — read, write, run, see of
    ///     the environment — when that is limited, as a build script's is
    ///     (docs/05 §5.6); null for everything, as a native program may.
    /// </summary>
    public SilSandbox? Sandbox { get; init; }

    /// <summary>The path a runtime call is to use, once the sandbox, if there is one, allows it.</summary>
    string PathFor(object? operand, SilAccess access) => Sandbox is { } sandbox ? sandbox.Check(Text(operand).Value, access) : Text(operand).Value;

    /// <summary>The last program <c>Runtime.run</c> ran: how it ended, and what it wrote to standard error.</summary>
    long _lastStatus;

    string _lastErrorOutput = "";

    /// <summary>Whether the last runtime call that can fail did.</summary>
    bool _ioFailed;

    /// <summary>Stops the program, as a trap, once cancelled: for a REPL, whose step limit is too high to wait for.</summary>
    public CancellationToken Interrupt { get; init; }

    /// <summary>
    ///     Heap objects allocated and not yet freed: after a whole program, a
    ///     leak — unless an immortal object keeps it alive, as the runtime's
    ///     report also leaves out.
    /// </summary>
    public int LiveObjects {
        get {
            if (State.Immortal.Count == 0) {
                return _live.Count;
            }

            var reached = new HashSet<Counted>(ReferenceEqualityComparer.Instance);
            foreach (var (type, immortal) in State.Immortal) {
                Reach(type, immortal, reached, root: true);
            }

            return _live.Count(c => !reached.Contains(c));
        }
    }

    /// <summary>Every counted object a value holds, and those they hold, strongly.</summary>
    static void Reach(SilType type, object? value, HashSet<Counted> reached, bool root = false) {
        if (value is null || type.IsTrivial) {
            return;
        }

        switch (type, value) {
            case (WeakStorageType or UnownedStorageType, _):
                return;

            case (ThickFunctionType, ClosureValue closure):
                Reach(SilType.Object, closure.Context, reached);
                return;

            case (_, Counted counted):
                if (!root && !reached.Add(counted)) {
                    return;
                }

                switch (counted) {
                    case HeapObject instance:
                        for (var i = 0; i < instance.Fields.Length; i++) {
                            Reach(instance.Type.Fields[i].Type, instance.Fields[i], reached);
                        }

                        break;

                    case HeapArray array:
                        foreach (var element in array.Elements) {
                            Reach(array.Type.Element, element, reached);
                        }

                        break;
                }

                return;

            case (StructType structType, object?[] fields):
                for (var i = 0; i < fields.Length; i++) {
                    Reach(structType.Fields[i].Type, fields[i], reached);
                }

                return;

            case (TupleType tuple, object?[] elements):
                for (var i = 0; i < elements.Length; i++) {
                    Reach(tuple.Elements[i], elements[i], reached);
                }

                return;

            case (EnumType enumType, EnumValue enumValue):
                if (enumType.Cases[enumValue.Case].Payload is { } payload) {
                    Reach(payload, enumValue.Payload, reached);
                }

                return;
        }
    }

    /// <summary>
    ///     '@immortal': each object the value holds is made immortal, its
    ///     count never touched again, and no longer one of the live objects.
    /// </summary>
    void MakeImmortal(SilType type, object? value) {
        if (value is null || type.IsTrivial) {
            return;
        }

        switch (type, value) {
            case (WeakStorageType or UnownedStorageType, _):
                return;

            case (ThickFunctionType, ClosureValue closure):
                MakeImmortal(SilType.Object, closure.Context);
                return;

            case (_, Counted counted):
                if (!Alive(counted).Immortal) {
                    counted.Immortal = true;
                    _live.Remove(counted);
                    State.Immortal.Add((type, counted));
                }

                return;

            case (StructType structType, object?[] fields):
                for (var i = 0; i < fields.Length; i++) {
                    MakeImmortal(structType.Fields[i].Type, fields[i]);
                }

                return;

            case (TupleType tuple, object?[] elements):
                for (var i = 0; i < elements.Length; i++) {
                    MakeImmortal(tuple.Elements[i], elements[i]);
                }

                return;

            case (EnumType enumType, EnumValue enumValue):
                if (enumType.Cases[enumValue.Case].Payload is { } payload) {
                    MakeImmortal(payload, enumValue.Payload);
                }

                return;
        }
    }

    /// <summary>How many instructions the last run carried out.</summary>
    public long Steps => _steps;

    /// <summary>What ARC did, counted as the runtime counts it: only what touches a count.</summary>
    public int Allocations { get; private set; }

    // What the program set and removed in its environment (a removal is
    // null): over the process's, or its sandbox's, never written to them.
    readonly Dictionary<string, string?> _environment = new(StringComparer.Ordinal);

    // An environment variable as the program sees it: its own writes first.
    string? VariableOf(string name) =>
        _environment.TryGetValue(name, out var own) ? own
        : Sandbox is { } sandbox ? sandbox.Environment(name)
        : Environment.GetEnvironmentVariable(name);

    public int Retains { get; private set; }

    /// <summary>How many times each coverage counter ran (<see cref="Coverage" />), as the native runtime writes them.</summary>
    public Dictionary<int, long> CoverageCounts { get; } = [];

    public int Releases { get; private set; }

    // =======================================================================
    // The heap
    // =======================================================================

    /// <summary>A counted heap object: a string, a class instance or an array buffer.</summary>
    internal abstract class Counted {
        public int Strong = 1;
        public bool Immortal;

        /// <summary>Made in a frame (docs/04 §3.7), as native code would.</summary>
        public bool OnStack;
        public bool Freed;
    }

    sealed class HeapString(string value) : Counted {
        List<Rune>? _scalars;
        byte[]? _utf8;
        public string Value { get; } = value;

        /// <summary>The string's scalars, decoded once: indexing by scalar is then constant time, as it is natively for ASCII.</summary>
        public List<Rune> Scalars => _scalars ??= [..Value.EnumerateRunes()];

        /// <summary>
        ///     The string's UTF-8 bytes, encoded once: what a cursor or a
        ///     String.Index counts in, as natively, so an index means the same
        ///     place in both.
        /// </summary>
        public byte[] Utf8 => _utf8 ??= Encoding.UTF8.GetBytes(Value);
    }

    sealed class HeapObject(ClassType type) : Counted {
        public ClassType Type { get; } = type;
        public object?[] Fields { get; } = new object?[type.Fields.Count];

        /// <summary>Unowned references to it: its memory stays until they go, though it may be deinited.</summary>
        public int Unowned;

        /// <summary>True once its last strong reference has gone and its deinit has run.</summary>
        public bool Deinited;

        /// <summary>True when its initialiser threw: no deinit of it runs when it goes.</summary>
        public bool Unmade;

        /// <summary>The entry its weak references share, made when the first is.</summary>
        public WeakEntry? Side;
    }

    /// <summary>What weak references to one object point to: the object, until it goes.</summary>
    sealed class WeakEntry(HeapObject target) {
        public HeapObject? Target = target;
        public int Count;
    }

    /// <summary>A region: the cells allocated in it, released in reverse when it goes.</summary>
    sealed class RegionObject : Counted {
        public List<RegionCell> Cells { get; } = [];

        /// <summary>Where each 'region frame = pool' block in progress began.</summary>
        public Stack<int> Marks { get; } = [];
    }

    /// <summary>A value in a region: storage that is gone once the region is.</summary>
    sealed class RegionCell(RegionObject region, SilType type) : IReference {
        object? _value;
        public SilType Type { get; } = type;

        /// <summary>Given back: the region was reset, or the block that took it ended.</summary>
        public bool Released;

        public object? Get() => Peek() ?? throw new SilTrap("load from uninitialised storage");

        public object? Peek() =>
            region.Freed || Released ? throw new SilTrap("use of a region's memory after the region ended") : _value;

        public void Set(object? value) => _value = region.Freed || Released ? throw new SilTrap("use of a region's memory after the region ended") : value;

        /// <summary>What the cell holds, for the region to destroy as it goes.</summary>
        public object? Take() {
            var value = _value;
            _value = null;
            return value;
        }
    }

    sealed class HeapArray(ArrayType type, List<object?> elements) : Counted {
        public ArrayType Type { get; } = type;
        public List<object?> Elements { get; } = elements;
    }

    /// <summary>
    ///     A slice: a run of an array's elements, seen where they are — the
    ///     buffer, which it holds a count of, or none for the empty slice.
    ///     A value, never changed in place: a write makes a new one.
    /// </summary>
    sealed record SliceValue(HeapArray? Buffer, int Start, int Count) {
        public static readonly SliceValue Empty = new(null, 0, 0);
    }

    /// <summary>The run of elements an array or a slice is: its buffer, where the run starts, and how long it is.</summary>
    static (HeapArray? Buffer, int Start, int Count) ElementsOf(object? value) =>
        value switch {
            HeapArray array => (Alive(array), 0, array.Elements.Count),
            SliceValue slice => (slice.Buffer is { } buffer ? Alive(buffer) : null, slice.Start, slice.Count),
            _ => (null, 0, 0),
        };

    T Allocate<T>(T value, bool onStack = false) where T : Counted {
        _live.Add(value);
        value.OnStack = onStack;
        if (!onStack) {
            Allocations++;
        }

        return value;
    }

    static T Alive<T>(T value) where T : Counted =>
        value.Freed ? throw new SilTrap("use of a freed object") : value;

    HeapString Text(object? value) => Alive((HeapString)value!);

    /// <summary>A copy of a value: every reference it holds retained.</summary>
    void Retain(SilType type, object? value) {
        if (value is null || type.IsTrivial) {
            return;
        }

        switch (type, value) {
            case (OpaqueType opaque, _):
                Retain(OpaqueTypeOf(opaque), value);
                return;

            case (WeakStorageType, WeakEntry entry):
                entry.Count++;
                return;

            case (UnownedStorageType, HeapObject held):
                held.Unowned++;
                return;

            case (_, Counted counted):
                if (!Alive(counted).Immortal) {
                    counted.Strong++;
                    Retains++;
                }

                return;

            case (ThickFunctionType, ClosureValue closure):
                Retain(SilType.Object, closure.Context);
                return;

            case (StructType structType, object?[] fields):
                for (var i = 0; i < fields.Length; i++) {
                    Retain(structType.Fields[i].Type, fields[i]);
                }

                return;

            case (TupleType tuple, object?[] elements):
                for (var i = 0; i < elements.Length; i++) {
                    Retain(tuple.Elements[i], elements[i]);
                }

                return;

            case (SliceType slice, SliceValue view):
                Retain(new ArrayType(slice.Element), view.Buffer);
                return;

            case (EnumType enumType, EnumValue enumValue):
                if (enumType.Cases[enumValue.Case].Payload is { } payload) {
                    Retain(payload, enumValue.Payload);
                }

                return;
        }
    }

    /// <summary>The end of a copy's life: every reference it holds released, and freed when the last goes.</summary>
    /// <remarks>
    ///     An uncounted release, <c>destroy_value [uncounted]</c>, is of an
    ///     object's only reference and not counted, as native code does not
    ///     count it; the releases its fields' destruction makes are.
    /// </remarks>
    /// <summary>
    ///     Whether storage holds something to destroy. An aggregate let go of
    ///     part by part — a struct initialiser's self, whose deinit must not
    ///     run — keeps its trivial parts, which need nothing.
    /// </summary>
    static bool Holds(SilType type, object? value) {
        switch (type, value) {
            case (_, null):
                return false;
            case (StructType structType, object?[] fields):
                for (var i = 0; i < fields.Length; i++) {
                    if (!structType.Fields[i].Type.IsTrivial && Holds(structType.Fields[i].Type, fields[i])) {
                        return true;
                    }
                }

                return false;
            case (TupleType tuple, object?[] elements):
                for (var i = 0; i < elements.Length; i++) {
                    if (!tuple.Elements[i].IsTrivial && Holds(tuple.Elements[i], elements[i])) {
                        return true;
                    }
                }

                return false;
            default:
                return true;
        }
    }

    void Release(SilType type, object? value, int depth, bool uncounted = false) {
        if (value is null || type.IsTrivial) {
            return;
        }

        switch (type, value) {
            case (OpaqueType opaque, _):
                Release(OpaqueTypeOf(opaque), value, depth, uncounted);
                return;

            case (ThickFunctionType, ClosureValue closure):
                Release(SilType.Object, closure.Context, depth);
                return;

            case (WeakStorageType, WeakEntry entry):
                if (--entry.Count == 0 && entry.Target is { } target) {
                    target.Side = null;
                }

                return;

            case (UnownedStorageType, HeapObject held):
                if (--held.Unowned == 0 && held.Deinited) {
                    held.Freed = true;
                    _live.Remove(held);
                }

                return;

            case (_, Counted counted):
                if (Alive(counted).Immortal) {
                    return;
                }

                // Native code counts an uncounted release only of an object
                // that is on the heap after all, which it releases as any.
                if (!uncounted || !counted.OnStack) {
                    Releases++;
                } else if (counted.Strong != 1) {
                    throw new SilTrap("an uncounted object was shared");
                }

                if (--counted.Strong > 0) {
                    return;
                }

                Free(counted, depth);
                return;

            case (StructType structType, object?[] fields):
                // A move-only struct's deinit sees it whole, before its fields go.
                if (structType.Deinit is { } deinit) {
                    var function = module.Find(deinit) ?? throw new SilTrap($"call to undefined @{deinit}");
                    Call(function, [fields], depth + 1);
                }

                for (var i = 0; i < fields.Length; i++) {
                    Release(structType.Fields[i].Type, fields[i], depth);
                }

                return;

            case (TupleType tuple, object?[] elements):
                for (var i = 0; i < elements.Length; i++) {
                    Release(tuple.Elements[i], elements[i], depth);
                }

                return;

            case (SliceType slice, SliceValue view):
                Release(new ArrayType(slice.Element), view.Buffer, depth);
                return;

            case (EnumType enumType, EnumValue enumValue):
                if (enumType.Cases[enumValue.Case].Payload is { } payload) {
                    Release(payload, enumValue.Payload, depth);
                }

                return;
        }
    }

    void Free(Counted counted, int depth) {
        switch (counted) {
            case HeapObject instance:
                // The deinits, the class's own and then its superclasses',
                // see the object alive; they may not keep it.
                for (var type = instance.Unmade ? null : instance.Type; type is not null; type = type.Superclass) {
                    if (type.Deinit is not { } deinit) {
                        continue;
                    }

                    counted.Strong = 1;
                    var function = module.Find(deinit) ?? throw new SilTrap($"call to undefined @{deinit}");
                    Call(function, [instance], depth + 1);
                    if (counted.Strong != 1) {
                        throw new SilTrap("an object outlived its deinit", located: false);
                    }
                }

                for (var i = instance.Fields.Length - 1; i >= 0; i--) {
                    Release(instance.Type.Fields[i].Type, instance.Fields[i], depth);
                }

                // Weak references now read as none; unowned ones hold the
                // memory, but not the object, until they go.
                instance.Deinited = true;
                if (instance.Side is { } side) {
                    side.Target = null;
                    instance.Side = null;
                }

                if (instance.Unowned > 0) {
                    return;
                }

                break;

            case HeapArray array:
                foreach (var element in array.Elements) {
                    Release(array.Type.Element, element, depth);
                }

                break;

            // A region destroys what was allocated in it, last first.
            case RegionObject region:
                for (var i = region.Cells.Count - 1; i >= 0; i--) {
                    Release(region.Cells[i].Type, region.Cells[i].Take(), depth);
                }

                break;
        }

        counted.Freed = true;
        _live.Remove(counted);
    }

    // =======================================================================
    // Storage
    // =======================================================================

    /// <summary>An address: something that can be read and written.</summary>
    interface IReference {
        object? Get();
        void Set(object? value);

        /// <summary>The value, or null when the storage is empty.</summary>
        object? Peek();
    }

    /// <summary>An <c>alloc_stack</c>.</summary>
    internal sealed class Slot : IReference {
        object? _value;
        public object? Get() => _value ?? throw new SilTrap("load from uninitialised storage");
        public void Set(object? value) => _value = value;
        public object? Peek() => _value;
    }

    /// <summary>
    ///     A field or element inside an aggregate at another address. Writing
    ///     replaces the whole aggregate with a changed copy, so that a struct
    ///     value someone else holds never changes under them.
    /// </summary>
    /// <remarks>
    ///     An initialiser fills an empty aggregate one field at a time, so
    ///     writing into one that holds nothing yet makes it; reading a part
    ///     that was never written traps.
    /// </remarks>
    sealed class Element(IReference parent, int index, int count) : IReference {
        public IReference Parent => parent;
        public int Index => index;

        public object? Get() => Peek() ?? throw new SilTrap("load from uninitialised storage");

        public object? Peek() => parent.Peek() is object?[] whole ? whole[index] : null;

        public void Set(object? value) {
            var copy = parent.Peek() is object?[] whole ? (object?[])whole.Clone() : new object?[count];
            copy[index] = value;
            parent.Set(copy);
        }
    }

    /// <summary>An element of an array, passed inout: the array is the only reference to its elements.</summary>
    sealed class ArrayElement(HeapArray array, int index) : IReference {
        public object? Get() => Peek() ?? throw new SilTrap("load from uninitialised storage");
        public object? Peek() => Alive(array).Elements[index];
        public void Set(object? value) => Alive(array).Elements[index] = value;
    }

    /// <summary>A field of an object.</summary>
    /// <summary>The accesses in progress (docs/02 §2.3): where, and whether each is a change.</summary>
    readonly List<(List<object> Storage, bool Modify)> _accesses = [];

    void Access(string name, IReference address) {
        if (!module.CheckExclusivity) {
            return;
        }

        if (name.StartsWith("access_check_", StringComparison.Ordinal)) {
            CheckAccess(address, name == "access_check_modify");
            return;
        }

        var storage = StorageOf(address);
        if (name == "access_end") {
            _accesses.RemoveAt(_accesses.FindLastIndex(a => a.Storage.SequenceEqual(storage)));
            return;
        }

        var modify = name == "access_begin_modify";
        CheckAccess(storage, modify);
        _accesses.Add((storage, modify));
    }

    /// <summary>A plain read or write of shared storage, as it happens: a trap if it overlaps an access in progress.</summary>
    void CheckAccess(IReference address, bool modify) {
        if (module.CheckExclusivity && _accesses.Count > 0) {
            CheckAccess(StorageOf(address), modify);
        }
    }

    void CheckAccess(List<object> storage, bool modify) {
        if (_accesses.Any(a => (modify || a.Modify) && Overlaps(a.Storage, storage))) {
            throw new SilTrap("overlapping accesses to the same storage");
        }
    }

    /// <summary>Two paths overlap when one leads into the other: a struct and a field of it, as the bytes do natively.</summary>
    static bool Overlaps(List<object> first, List<object> second) {
        for (var i = 0; i < Math.Min(first.Count, second.Count); i++) {
            if (!Equals(first[i], second[i])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     What an address is the storage of, as a path: an object and its
    ///     field — the same whichever reference reached it — or a global, then
    ///     the fields and elements inside.
    /// </summary>
    static List<object> StorageOf(IReference address) {
        switch (address) {
            case Field field:
                return [field.Instance, field.Index];
            case Element element:
                var path = StorageOf(element.Parent);
                path.Add(element.Index);
                return path;
            default:
                return [address];
        }
    }

    sealed class Field(HeapObject instance, int index) : IReference {
        public HeapObject Instance => instance;
        public int Index => index;
        public object? Get() => Peek() ?? throw new SilTrap("load from uninitialised storage");
        public object? Peek() => Alive(instance).Fields[index];
        public void Set(object? value) => Alive(instance).Fields[index] = value;
    }

    /// <summary>An enum value: which case, and its payload.</summary>
    public sealed record EnumValue(int Case, object? Payload);

    /// <summary>What <see cref="GlobalValue" /> gives for a part no literal can say: an object, an array, a function.</summary>
    public static readonly object Opaque = new();

    /// <summary>
    ///     What the global holds, as plain values: numbers, <see cref="bool" />,
    ///     <see cref="Rune" />, strings as .NET strings, an array of parts for
    ///     a struct or tuple, an <see cref="EnumValue" />; anything else is
    ///     <see cref="Opaque" />. Null when nothing is stored there.
    /// </summary>
    public object? GlobalValue(string name) {
        static object? Plain(object? value) =>
            value switch {
                null or long or ulong or bool or double or float or Rune => value,
                HeapString text => text.Value,
                object?[] parts => parts.Select(Plain).ToArray(),
                EnumValue e => e with { Payload = Plain(e.Payload) },
                _ => Opaque,
            };

        return _globals.TryGetValue(name, out var slot) ? Plain(slot.Peek()) : null;
    }

    /// <summary>A closure: its code, and the context it captured, or null.</summary>
    sealed record ClosureValue(string Function, object? Context);

    /// <summary>What a throwing call gives back instead of a result.</summary>
    sealed record Thrown(object? Error);

    // =======================================================================
    // Running
    // =======================================================================

    /// <summary>
    ///     Runs a function and gives its result, a string as a .NET string.
    ///     The result is released, so that <see cref="LiveObjects" /> counts
    ///     only what the program leaked.
    /// </summary>
    public object? Run(string function, params object[] arguments) {
        if (module.Find(function) is not { } target) {
            throw new ArgumentException($"no function @{function}", nameof(function));
        }

        // Each SIL call is a .NET call here, and a deep program — a parser
        // given 500 nested arrays — needs more stack than a thread is given
        // by default. The depth limit, not the stack, is what stops a
        // runaway recursion.
        object? result = null;
        Exception? failure = null;
        var thread = new Thread(() => {
                try {
                    result = RunHere(target, arguments);
                } catch (Exception exception) {
                    failure = exception;
                }
            },
            512 * 1024 * 1024
        );
        thread.Start();
        thread.Join();
        if (failure is not null) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    object? RunHere(SilFunction target, object[] arguments) {

        _steps = 0;
        var passed = arguments.Select(static a => a is string s ? new HeapString(s) { Immortal = true } : a).ToArray();
        object? result;
        try {
            result = Call(target, passed, 0);
            if (result is Thrown thrown) {
                throw new SilTrap($"uncaught error: {Format(thrown.Error)}");
            }
        } catch (SilTrap trap) {
            RunTrapHook(trap);
            throw;
        }

        var plain = result is HeapString text ? Alive(text).Value : result;
        Release(target.Type.Result, result, 0);

        // The hook let go of as the program ends, as the runtime does before it counts what is left.
        if (_trapHook is { } hook) {
            _trapHook = null;
            Release(new ThickFunctionType(TrapHookSignature), hook, 0);
        }

        return plain;
    }

    /// <summary>The panic hook the program set (<c>trap_hook_set</c>), or null for the default report.</summary>
    ClosureValue? _trapHook;

    static readonly FunctionType TrapHookSignature = new([SilType.String, SilType.String, new ArrayType(SilType.String)], SilType.Unit);

    /// <summary>
    ///     A trap said by the program's panic hook, as the runtime says it:
    ///     the hook called with the message, the place and no frames (the
    ///     interpreter has none to give), and the trap's own report then
    ///     left out. A trap in the hook gives the default report of the
    ///     trap it was given, and then its own.
    /// </summary>
    void RunTrapHook(SilTrap trap) {
        if (_trapHook is not { } hook || trap.Exits) {
            return;
        }

        _trapHook = null;
        var message = Allocate(new HeapString(trap.Message));
        var site = Allocate(new HeapString(trap.Site ?? ""));
        var frames = Allocate(new HeapArray(new ArrayType(SilType.String), []));
        try {
            _steps = 0;
            var (function, passed) = Callee(hook, [message, site, frames]);
            Call(function, passed, 0);
            trap.Hooked = true;
        } catch (SilTrap inner) {
            trap.HookTrap = inner;
        }
    }

    object? Call(SilFunction function, IReadOnlyList<object?> arguments, int depth) {
        if (depth > depthLimit) {
            throw new SilTrap("stack overflow: calls nested too deeply", located: false);
        }

        SilInstruction? current = null;

        // Unspecialised code's values are copied and destroyed as the metadata it is given says.
        var outer = _opaqueTypes;
        var outerAssociated = _associatedTypes;
        if (function.HiddenFrame is { } hiddenFrame && arguments is [HeapObject frame, ..]) {
            // An unspecialised async function's resume or drop function: given what its frame keeps.
            _opaqueTypes = [];
            for (var i = 0; i < hiddenFrame.Fields.Count; i++) {
                if (hiddenFrame.Fields[i].Name.StartsWith("$metadata.", StringComparison.Ordinal) && frame.Fields[i] is MetadataModel model) {
                    _opaqueTypes[hiddenFrame.Fields[i].Name["$metadata.".Length..]] = model;
                }
            }

            _associatedTypes = function.AssociatedMetadata.Count > 0 ? (function, name => hiddenFrame.FieldIndex(name) is var at and >= 0 ? frame.Fields[at] : null) : null;
        } else if (OpaqueParametersOf(function) is { Length: > 0 } opaque) {
            _opaqueTypes = [];
            foreach (var (index, name) in opaque) {
                if (index < arguments.Count && arguments[index] is MetadataModel model) {
                    _opaqueTypes[name] = model;
                }
            }

            _associatedTypes = function.AssociatedMetadata.Count > 0
                ? (function, name => function.ParameterNames.IndexOf(name) is var at and >= 0 && at < arguments.Count ? arguments[at] : null)
                : null;
        }

        try {
            return Run(function, arguments, depth, ref current);
        } catch (SilTrap trap) when (trap is { Site: null, Located: true }) {
            // The innermost call that sees it says where: the instruction
            // that trapped, as the native program's trap does.
            trap.Site = current?.TrapSite(function);

            throw;
        } finally {
            _opaqueTypes = outer;
            _associatedTypes = outerAssociated;
        }
    }

    object? Run(SilFunction function, IReadOnlyList<object?> arguments, int depth, ref SilInstruction? current) {
        var values = new Dictionary<SilValue, object?>();
        var block = function.Entry;
        IReadOnlyList<object?> incoming = arguments;

        while (true) {
            for (var i = 0; i < block.Arguments.Count; i++) {
                values[block.Arguments[i]] = incoming[i];
            }

            SilBlock? next = null;
            foreach (var instruction in block.Instructions) {
                current = instruction;
                if (++_steps > stepLimit) {
                    throw new SilTrap("step limit reached", located: false);
                }

                // Asked every so many steps, not every one: a REPL's Ctrl-C.
                if ((_steps & 0xFFFF) == 0 && Interrupt.IsCancellationRequested) {
                    throw new SilTrap("interrupted");
                }

                object? Get(SilValue value) => values[value];

                switch (instruction) {
                    case IntegerLiteralInst literal:
                        values[literal] = literal.Type == SilType.Bool ? literal.Value != 0
                            : literal.Type == SilType.Char ? new Rune((int)literal.Value)
                            : literal.Type == SilType.U64 || literal.Type == SilType.UInt ? unchecked((ulong)literal.Value)
                            : literal.Type == SilType.I128 ? literal.WideValue
                            : literal.Type == SilType.U128 ? unchecked((UInt128)literal.WideValue)
                            : literal.Value;
                        break;

                    case FloatLiteralInst literal:
                        values[literal] = literal.Type == SilType.F32 ? (float)literal.Value
                            : literal.Type == SilType.F16 ? (Half)literal.Value
                            : literal.Type == SilType.BF16 ? BFloat16.FromDouble(literal.Value)
                            : (object)literal.Value;
                        break;

                    case MetadataInst metadata:
                        values[metadata] = MetadataOf(SilType.MentionsOpaque(metadata.Described) ? Concrete(metadata.Described) : metadata.Described);
                        break;

                    case StringLiteralInst literal:
                        if (!_literals.TryGetValue(literal, out var immortal)) {
                            _literals[literal] = immortal = new HeapString(literal.Value) { Immortal = true };
                        }

                        values[literal] = immortal;
                        break;

                    case BuiltinInst { Name: var name } builtin when name.StartsWith("print_", StringComparison.Ordinal):
                        Output.Append(Format(Get(builtin.Operands[0]))).Append('\n');
                        break;

                    case BuiltinInst builtin:
                        values[builtin] = Perform(builtin, [..builtin.Operands.Select(Get)], depth);
                        break;

                    case AllocStackInst alloc:
                        values[alloc] = new Slot();
                        break;

                    case DeallocStackInst dealloc:
                        if (dealloc.Address is AllocStackInst { Unmanaged: false } deallocated && !deallocated.ElementType.IsTrivial
                            && Holds(deallocated.ElementType, ((IReference)Get(dealloc.Address)!).Peek())) {
                            throw new SilTrap("dealloc_stack of storage still holding a value");
                        }

                        break;

                    case LoadInst load: {
                        var address = (IReference)Get(load.Address)!;

                        // Empty storage for an array is the empty array — and so for
                        // a type parameter's value, which may be one (#297).
                        // A struct with no fields is whole before anything is stored: an initialiser's self.
                        var loaded = load.Type is StructType { Fields.Count: 0 }
                            ? address.Peek() ?? Array.Empty<object?>()
                            : load.Type is ArrayType or OpaqueType || address is Field { Instance.Type.IsCoroutineFrame: true } or FrameTail ? address.Peek() : address.Get();
                        switch (load.Kind) {
                            case LoadKind.Copy:
                                Retain(load.Type, loaded);
                                break;
                            case LoadKind.Take:
                                address.Set(null);
                                break;
                        }

                        values[load] = loaded;
                        break;
                    }

                    case StoreInst store: {
                        var address = (IReference)Get(store.Address)!;
                        var old = address.Peek();
                        if (!store.Value.Type.IsTrivial && store.Kind == StoreKind.Init && old is not null && address is not (Field { Instance.Type.IsCoroutineFrame: true } or FrameTail)
                            // A slot that holds without owning (borrow-lets' in a loop, stored each turn
                            // and never destroyed) has nothing to leak: the store takes nothing, as the
                            // verifier has it, and natively writes over the last borrow.
                            && store.Address is not AllocStackInst { Unmanaged: true }
                            // An opaque value of a trivial type, written where its caller left one: bits over bits, as natively.
                            && !(SilType.MentionsOpaque(store.Value.Type) && Concrete(store.Value.Type).IsTrivial)) {
                            throw new SilTrap("store [init] into storage that holds a value");
                        }

                        address.Set(Get(store.Value));
                        if (store.Kind == StoreKind.Assign && store.Address is not AllocStackInst { Unmanaged: true }) {
                            Release(store.Value.Type, old, depth);
                        }

                        break;
                    }

                    case LoadWeakInst weak: {
                        var optional = (EnumType)weak.Type;
                        if (((IReference)Get(weak.Address)!).Peek() is WeakEntry { Target: { } target }) {
                            Retain(SilType.Object, target);
                            values[weak] = new EnumValue(optional.CaseIndex("Some"), target);
                        } else {
                            values[weak] = new EnumValue(optional.CaseIndex("None"), null);
                        }

                        break;
                    }

                    case StoreWeakInst weak: {
                        var address = (IReference)Get(weak.Address)!;
                        var storage = (WeakStorageType)((AddressType)weak.Address.Type).Pointee;
                        WeakEntry? entry = null;
                        if (Get(weak.Value) is EnumValue { Payload: HeapObject target }) {
                            entry = target.Side ??= new WeakEntry(target);
                            entry.Count++;
                        }

                        var old = address.Peek();
                        address.Set(entry);
                        Release(storage, old, depth);
                        break;
                    }

                    case WeakCopyValueInst weak: {
                        WeakEntry? entry = null;
                        if (Get(weak.Value) is EnumValue { Payload: HeapObject target }) {
                            entry = target.Side ??= new WeakEntry(target);
                            entry.Count++;
                        }

                        values[weak] = entry;
                        break;
                    }

                    case UnownedCopyValueInst unowned: {
                        var held = (HeapObject)Get(unowned.Value)!;
                        held.Unowned++;
                        values[unowned] = held;
                        break;
                    }

                    case StrongCopyWeakValueInst weak: {
                        var optional = (EnumType)weak.Type;
                        if (Get(weak.Value) is WeakEntry { Target: { } target }) {
                            Retain(SilType.Object, target);
                            values[weak] = new EnumValue(optional.CaseIndex("Some"), target);
                        } else {
                            values[weak] = new EnumValue(optional.CaseIndex("None"), null);
                        }

                        break;
                    }

                    case StrongCopyUnownedValueInst unowned: {
                        var held = (HeapObject)Get(unowned.Value)!;
                        if (held.Deinited) {
                            throw new SilTrap("read an unowned reference to an object that is gone");
                        }

                        Retain(SilType.Object, held);
                        values[unowned] = held;
                        break;
                    }

                    case LoadUnownedInst unowned: {
                        var held = (HeapObject)((IReference)Get(unowned.Address)!).Get()!;
                        if (held.Deinited) {
                            throw new SilTrap("read an unowned reference to an object that is gone");
                        }

                        Retain(SilType.Object, held);
                        values[unowned] = held;
                        break;
                    }

                    case StoreUnownedInst unowned: {
                        var address = (IReference)Get(unowned.Address)!;
                        var storage = (UnownedStorageType)((AddressType)unowned.Address.Type).Pointee;
                        var held = (HeapObject)Get(unowned.Value)!;
                        held.Unowned++;
                        var old = address.Peek();
                        address.Set(held);
                        Release(storage, old, depth);
                        break;
                    }

                    // Borrowed: no count changes; the storage keeps it alive.
                    case LoadBorrowInst borrow: {
                        var address = (IReference)Get(borrow.Address)!;
                        values[borrow] = borrow.Type is StructType { Fields.Count: 0 } ? address.Peek() ?? Array.Empty<object?>() : borrow.Type is ArrayType or OpaqueType ? address.Peek() : address.Get();
                        break;
                    }

                    case EndBorrowInst:
                        break;

                    case DestroyAddrInst destroy: {
                        var address = (IReference)Get(destroy.Address)!;
                        var old = address.Peek();
                        address.Set(null);
                        if (destroy.Address is AllocStackInst { Unmanaged: true }) {
                            break;
                        }

                        Release(((AddressType)destroy.Address.Type).Pointee, old, depth, destroy.Uncounted);
                        break;
                    }

                    case CopyValueInst copy:
                        Retain(copy.Type, Get(copy.Value));
                        values[copy] = Get(copy.Value);
                        break;

                    case DestroyValueInst destroy:
                        Release(destroy.Value.Type, Get(destroy.Value), depth, destroy.Uncounted);
                        break;

                    case AllocRefInst alloc:
                        // A generic class of types only metadata knows (#297): the class it is for the types the function was given.
                        values[alloc] = alloc.ClassType.IsCoroutineFrame ? PushFrame(alloc.ClassType)
                            : Allocate(new HeapObject(alloc.ClassType.IsGenericInstance && SilType.MentionsOpaque(alloc.ClassType) ? (ClassType)Concrete(alloc.ClassType) : alloc.ClassType), alloc.OnStack);
                        break;

                    case RefElementAddrInst element:
                        values[element] = new Field(Alive((HeapObject)Get(element.Reference)!), element.Field);
                        break;

                    case StructInst or TupleInst:
                        values[instruction] = instruction.Operands.Select(Get).ToArray();
                        break;

                    case StructExtractInst extract:
                        values[extract] = ((object?[])Get(extract.Value)!)[extract.Field];
                        break;

                    case TupleExtractInst extract:
                        values[extract] = ((object?[])Get(extract.Value)!)[extract.Element];
                        break;

                    case StructElementAddrInst element: {
                        var structType = (StructType)((AddressType)element.Address.Type).Pointee;
                        values[element] = new Element((IReference)Get(element.Address)!, element.Field, structType.Fields.Count);
                        break;
                    }

                    case TupleElementAddrInst element: {
                        var tupleType = (TupleType)((AddressType)element.Address.Type).Pointee;
                        values[element] = new Element((IReference)Get(element.Address)!, element.Element, tupleType.Elements.Length);
                        break;
                    }

                    case EnumInst enumInst:
                        values[enumInst] = new EnumValue(enumInst.Case, enumInst.Payload is { } payload ? Get(payload) : null);
                        break;

                    case SwitchEnumInst switchEnum: {
                        // Storage that was never written reads as the first case, as zeroed memory does natively.
                        var scrutinee = (EnumValue?)Get(switchEnum.Value) ?? throw new SilTrap("switch_enum on an empty value");
                        var match = switchEnum.Cases.FirstOrDefault(c => c.Case == scrutinee.Case);
                        if (match.Target is { } target) {
                            next = target;
                            incoming = target.Arguments.Count == 0 ? [] : [scrutinee.Payload];
                        } else {
                            next = switchEnum.Default ?? throw new SilTrap("switch_enum matched no case");
                            incoming = [];
                        }

                        break;
                    }

                    case CondFailInst condFail:
                        if ((bool)Get(condFail.Condition)!) {
                            throw new SilTrap(condFail.Message);
                        }

                        break;

                    case ThrowInst throwInst:
                        return new Thrown(Get(throwInst.Error));

                    case TryApplyInst tryApply: {
                        var (callee, passed) = Callee(Get(tryApply.Callee)!, tryApply.Arguments.Select(Get));
                        var result = Call(callee, passed, depth + 1);
                        if (result is Thrown error) {
                            next = tryApply.Error;
                            incoming = [error.Error];
                        } else {
                            next = tryApply.Normal;
                            incoming = tryApply.Normal.Arguments.Count == 0 ? [] : [result];
                        }

                        break;
                    }

                    case FunctionRefInst reference:
                        values[reference] = reference.Function;
                        break;

                    case GlobalAddrInst global:
                        if (!_globals.TryGetValue(global.Global, out var globalSlot)) {
                            _globals[global.Global] = globalSlot = new Slot();
                        }

                        // A session's global is set up as it is first reached.
                        if (Carried is not null && State.Initialised.Add(global.Global)
                            && module.Find(SilGen.GlobalInitPrefix + global.Global) is { } setUp) {
                            Call(setUp, [], depth + 1);
                        }

                        values[global] = globalSlot;
                        break;

                    case PartialApplyInst partial:
                        values[partial] = new ClosureValue((string)Get(partial.Function)!, partial.Context is { } context ? Get(context) : null);
                        break;

                    case UncheckedRefCastInst cast:
                        values[cast] = Get(cast.Value);
                        break;

                    case UpcastInst upcast:
                        values[upcast] = Get(upcast.Value);
                        break;

                    // The method for the class the object is, not the one it is seen as.
                    case ClassMethodInst method: {
                        var instance = Alive((HeapObject)Get(method.Reference)!);
                        var slot = instance.Type.MethodIndex(method.Method);
                        values[method] = slot >= 0 ? instance.Type.VTable[slot].Function : throw new SilTrap($"no method #{method.Method}");
                        break;
                    }

                    case ApplyInst apply: {
                        var (callee, passed) = Callee(Get(apply.Callee)!, apply.Arguments.Select(Get));
                        values[apply] = Call(callee, passed, depth + 1);
                        break;
                    }

                    case BranchInst branch:
                        incoming = [..branch.Operands.Select(Get)];
                        next = branch.Target;
                        break;

                    case CondBranchInst conditional:
                        if ((bool)Get(conditional.Condition)!) {
                            incoming = [..conditional.TrueArguments.Select(Get)];
                            next = conditional.WhenTrue;
                        } else {
                            incoming = [..conditional.FalseArguments.Select(Get)];
                            next = conditional.WhenFalse;
                        }

                        break;

                    case ReturnInst returnInst:
                        return returnInst.Value is { } value ? Get(value) : null;

                    case UnreachableInst:
                        throw new SilTrap("reached 'unreachable'");
                }
            }

            block = next ?? throw new SilTrap("fell off the end of a block");
        }
    }

    /// <summary>What a call runs: a function by name, or a closure's code with its context last.</summary>
    (SilFunction Function, object?[] Arguments) Callee(object callee, IEnumerable<object?> arguments) {
        var (name, context, hasContext) = callee switch {
            ClosureValue closure => (closure.Function, closure.Context, true),
            _ => ((string)callee, null, false),
        };

        var function = module.Find(name) ?? throw new SilTrap(module.Foreign.TryGetValue(name, out var foreign)
            ? $"'{foreign.Symbol}' is a C function, which the interpreter cannot call; build the program to run it"
            : $"call to undefined @{name}");
        var passed = arguments.ToList();
        if (hasContext) {
            passed.Add(context);
        }

        return (function, passed.ToArray());
    }

    // =======================================================================
    // Builtins
    // =======================================================================

    object? Perform(BuiltinInst builtin, object?[] operands, int depth) {
        switch (builtin.Name) {
            // Counted as the native runtime counts it, so that a program
            // interpreted reports the coverage it would natively.
            case Coverage.Builtin:
                var counter = Convert.ToInt32(operands[0], System.Globalization.CultureInfo.InvariantCulture);
                CoverageCounts[counter] = CoverageCounts.GetValueOrDefault(counter) + 1;
                return null;

            case "array_empty":
                return builtin.Type is SliceType ? SliceValue.Empty : null;

            case "no_context":
                return null;

            // A fresh array of the constants, room for them alone, as the
            // native constant has.
            case "array_literal" or "array_literal_stack":
                return Allocate(new HeapArray((ArrayType)Concrete(builtin.Type), new List<object?>(operands)));

            // The whole of an array, viewed: the buffer shared, not copied.
            // The array moved into the view, which holds its buffer, empty or not.
            case "slice_of_take":
                return operands[0] is HeapArray wholeArray ? new SliceValue(Alive(wholeArray), 0, wholeArray.Elements.Count) : SliceValue.Empty;

            case "slice_of" or "slice_of_borrowed": {
                if (operands[0] is not HeapArray array || Alive(array).Elements.Count == 0) {
                    return SliceValue.Empty;
                }

                if (builtin.Name == "slice_of") {
                    Retain(builtin.Operands[0].Type, array);
                }

                return new SliceValue(array, 0, array.Elements.Count);
            }

            case "region_create":
                return Allocate(new RegionObject());

            case "region_reset" or "region_pop": {
                var region = Alive((RegionObject)operands[0]!);
                var keep = builtin.Name == "region_reset" ? 0 : region.Marks.Count > 0 ? region.Marks.Pop() : region.Cells.Count;
                if (builtin.Name == "region_reset") {
                    region.Marks.Clear();
                }

                for (var i = region.Cells.Count - 1; i >= keep; i--) {
                    Release(region.Cells[i].Type, region.Cells[i].Take(), depth);
                    region.Cells[i].Released = true;
                }

                region.Cells.RemoveRange(keep, region.Cells.Count - keep);
                return null;
            }

            case "region_push":
                Alive((RegionObject)operands[0]!).Marks.Push(((RegionObject)operands[0]!).Cells.Count);
                return null;

            case "region_alloc": {
                var region = Alive((RegionObject)operands[0]!);
                var cell = new RegionCell(region, ((AddressType)builtin.Type).Pointee);
                region.Cells.Add(cell);
                return cell;
            }

            case "string_concat":
                return Allocate(new HeapString(Text(operands[0]).Value + Text(operands[1]).Value));

            case "string_concat_many":
                return Allocate(new HeapString(string.Concat(operands.Select(o => Text(o).Value))));

            case "ref_identical":
                return ReferenceEquals(Alive((HeapObject)operands[0]!), Alive((HeapObject)operands[1]!));

            case "ref_is_unique":
                return (((IReference)operands[0]!).Peek() is object?[] { Length: 1 } holder ? holder[0] : ((IReference)operands[0]!).Peek()) is Counted { Strong: 1, Immortal: false };

            case "make_immortal":
                MakeImmortal(builtin.Operands[0].Type, operands[0]);
                return null;

            // One thread runs every task here: nothing is shared between threads.
            case "share" or "share_into" or "share_into_at" or "share_global" or "share_global_at":
                return null;

            case "object_unmade":
                Alive((HeapObject)operands[0]!).Unmade = true;
                return null;

            // An actor's identity: the interpreter's hops never wait, so need none.
            case "address_to_int":
                return 0L;

            // Nothing to keep from an interpreter, which optimises nothing.
            case SilBuiltins.BlackBox:
                return null;

            case "string_count":
                return (long)Text(operands[0]).Scalars.Count;

            case "string_join": {
                var parts = operands[0] is HeapArray given ? Alive(given).Elements.Select(p => Text(p).Value) : [];
                return Allocate(new HeapString(string.Join(Text(operands[1]).Value, parts)));
            }

            case "string_hash":
                return StringHash(Encoding.UTF8.GetBytes(Text(operands[0]).Value), (long)operands[1]!);

            case "string_hash_range":
                return StringHash(ByteRange(operands, 0).ToArray(), (long)operands[3]!);

            case "string_count_range": {
                var count = 0L;
                foreach (var b in ByteRange(operands, 0)) {
                    if (!IsContinuation(b)) {
                        count++;
                    }
                }

                return count;
            }

            case "string_compare_range":
                return (long)Math.Sign(ByteRange(operands, 0).SequenceCompareTo(ByteRange(operands, 3)));

            case "string_utf8_find_range": {
                var found = ByteRange(operands, 0).IndexOf(ByteRange(operands, 3));
                return found < 0 ? -1L : (long)operands[1]! + found;
            }

            case "string_utf8_find_any": {
                var text = ByteRange(operands, 0);
                var ranges = System.Text.Encoding.UTF8.GetBytes((string)operands[3]!);
                for (var i = 0; i < text.Length; i++) {
                    for (var k = 0; k + 1 < ranges.Length; k += 2) {
                        if (text[i] >= ranges[k] && text[i] <= ranges[k + 1]) {
                            return (long)operands[1]! + i;
                        }
                    }
                }

                return -1L;
            }

            case "string_utf8_find_last_range": {
                var text = ByteRange(operands, 0);
                var part = ByteRange(operands, 3);
                if (part.Length > text.Length) {
                    return -1L;
                }

                var found = part.Length == 0 ? text.Length : text.LastIndexOf(part);
                return found < 0 ? -1L : (long)operands[1]! + found;
            }

            case "hash_seed":
                return HashSeed;

            // The library's portable code is what the interpreter runs.
            case "codec_bytes_bytes" or "codec_string_bytes" or "codec_bytes_raw" or "utf8_to_utf16" or "utf16_to_utf8":
                return 0L;

            case "crypto_blocks" or "crypto_blocks_raw" or "vector_available":
                return false;

            case "random_bits":
                return BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

            // Scalar by scalar, as the runtime's byte search finds the same place.
            case "string_find": {
                var text = Text(operands[0]).Scalars;
                var part = Text(operands[1]).Scalars;
                var from = Math.Max(0L, (long)operands[2]!);
                for (var i = from; i + part.Count <= text.Count; i++) {
                    var k = 0;
                    while (k < part.Count && text[(int)i + k] == part[k]) {
                        k++;
                    }

                    if (k == part.Count) {
                        return i;
                    }
                }

                return -1L;
            }

            // UTF-8's byte order is the scalars' order, as the runtime compares.
            case "string_compare":
                return (long)Math.Sign(Encoding.UTF8.GetBytes(Text(operands[0]).Value).AsSpan()
                    .SequenceCompareTo(Encoding.UTF8.GetBytes(Text(operands[1]).Value)));

            case "string_at": {
                var scalars = Text(operands[0]).Scalars;
                var index = (long)operands[1]!;
                return index >= 0 && index < scalars.Count ? scalars[(int)index] : throw new SilTrap("string index out of range");
            }

            case "string_slice": {
                var scalars = Text(operands[0]).Scalars;
                var (from, to) = ((long)operands[1]!, (long)operands[2]!);
                if (from < 0 || to > scalars.Count || from > to) {
                    throw new SilTrap("string slice out of range");
                }

                return Allocate(new HeapString(string.Concat(scalars.Skip((int)from).Take((int)(to - from)))));
            }

            case "string_contains":
                return Text(operands[0]).Scalars.Contains((Rune)operands[1]!);

            case "string_index_of":
                return (long)Text(operands[0]).Scalars.IndexOf((Rune)operands[1]!);

            // The cursor is a scalar index here, a byte offset natively;
            // nothing but these three reads it.
            // A cursor, as natively, is a byte offset.
            case "string_cursor_end" or "string_utf8_count":
                return (long)Text(operands[0]).Utf8.Length;

            case "string_cursor_scalar":
                return DecodeUtf8(Text(operands[0]).Utf8, (int)(long)operands[1]!).Scalar;

            case "string_cursor_next": {
                var at = (int)(long)operands[1]!;
                return (long)(at + DecodeUtf8(Text(operands[0]).Utf8, at).Width);
            }

            case "string_cursor_previous": {
                var bytes = Text(operands[0]).Utf8;
                var at = (int)(long)operands[1]! - 1;
                while (at > 0 && IsContinuation(bytes[at])) {
                    at--;
                }

                return (long)at;
            }

            case "string_utf8_at": {
                var bytes = Text(operands[0]).Utf8;
                var at = (long)operands[1]!;
                return at >= 0 && at < bytes.Length ? (long)bytes[at] : throw new SilTrap("string index out of range");
            }

            case "string_utf8_load_U64": {
                var bytes = Text(operands[0]).Utf8;
                var at = (long)operands[1]!;
                return at >= 0 && at <= bytes.Length - 8
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)at))
                    : throw new SilTrap("string index out of range");
            }

            case "string_index_scalar":
                return DecodeUtf8(Text(operands[0]).Utf8, ScalarStart(Text(operands[0]).Utf8, (long)operands[1]!)).Scalar;

            case "string_index_after": {
                var bytes = Text(operands[0]).Utf8;
                var at = ScalarStart(bytes, (long)operands[1]!);
                return (long)(at + DecodeUtf8(bytes, at).Width);
            }

            case "string_index_before": {
                var bytes = Text(operands[0]).Utf8;
                var at = (long)operands[1]!;
                if (at <= 0 || at > bytes.Length) {
                    throw new SilTrap("string index out of range");
                }

                if (!IsBoundary(bytes, at)) {
                    throw new SilTrap("string index not on a character boundary");
                }

                do {
                    at--;
                } while (IsContinuation(bytes[at]));

                return at;
            }

            case "string_utf8_slice": {
                var bytes = Text(operands[0]).Utf8;
                var (from, to) = ((long)operands[1]!, (long)operands[2]!);
                if (from < 0 || to > bytes.Length || from > to) {
                    throw new SilTrap("string slice out of range");
                }

                if (!IsBoundary(bytes, from) || !IsBoundary(bytes, to)) {
                    throw new SilTrap("string index not on a character boundary");
                }

                return Allocate(new HeapString(Encoding.UTF8.GetString(bytes, (int)from, (int)(to - from))));
            }

            case "string_utf8_find": {
                var text = Text(operands[0]).Utf8;
                var from = Math.Max(0L, (long)operands[2]!);
                if (from > text.Length) {
                    return -1L;
                }

                var found = text.AsSpan((int)from).IndexOf(Text(operands[1]).Utf8);
                return found < 0 ? -1L : from + found;
            }

            case "string_utf8_find_last": {
                var text = Text(operands[0]).Utf8;
                var part = Text(operands[1]).Utf8;
                var to = Math.Min((long)operands[2]!, text.Length);
                return to - part.Length < 0 ? -1L : part.Length == 0 ? to : (long)text.AsSpan(0, (int)to).LastIndexOf(part);
            }

            case "bytes_find":
                return (long)BytesOf(operands[0]).AsSpan().IndexOf((byte)(long)operands[1]!);

            case "bytes_find_any":
                return (long)BytesOf(operands[0]).AsSpan().IndexOfAny(BytesOf(operands[1]));

            case "bytes_count":
                return (long)BytesOf(operands[0]).AsSpan().Count((byte)(long)operands[1]!);

            case "utf8_validate": {
                var bytes = BytesOf(operands[0]);
                var valid = Utf8.IsValid(bytes);
                if (valid) {
                    return -1L;
                }

                // .NET's decoder stops at the first ill-formed sequence, as the runtime's check does.
                Utf8.ToUtf16(bytes, new char[bytes.Length], out var read, out _, replaceInvalidSequences: false);
                return (long)read;
            }

            case "string_from_utf8_unchecked":
                return Allocate(new HeapString(Encoding.UTF8.GetString(BytesOf(operands[0]))));

            case "string_buffer_make" or "string_buffer_grow" or "string_buffer_copy" or "string_buffer_valid" or "string_buffer_take":
                return StringBufferBuiltin(builtin, operands);

            case "string_from_ascii_words": {
                var bytes = new byte[40];
                for (var word = 0; word < 5; word++) {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8 * word), (ulong)operands[word]!);
                }

                return Allocate(new HeapString(Encoding.ASCII.GetString(bytes, 0, (int)(long)operands[5]!)));
            }

            case "string_trimmed": {
                var scalars = Text(operands[0]).Scalars;
                var start = 0;
                var end = scalars.Count;
                while (start < end && IsWhitespace(scalars[start])) {
                    start++;
                }

                while (end > start && IsWhitespace(scalars[end - 1])) {
                    end--;
                }

                return Allocate(new HeapString(string.Concat(scalars.Skip(start).Take(end - start))));
            }

            case "string_split": {
                var separator = ((Rune)operands[1]!).ToString();
                var parts = Text(operands[0]).Value.Split(separator);
                return Allocate(new HeapArray(new ArrayType(SilType.String), [..parts.Select(p => (object?)Allocate(new HeapString(p)))]));
            }

            case "io_write":
                Output.Append(Text(operands[0]).Value);
                return null;

            case "io_write_error":
                ErrorOutput.Append(Text(operands[0]).Value);
                return null;

            // The place is the caller's, which the trap says rather than its own.
            case "trap": {
                // An empty site is left out, as the native trap leaves it:
                // not the place of this instruction, which is the library's.
                var site = Text(operands[1]).Value;
                throw site.Length == 0
                    ? new SilTrap(Text(operands[0]).Value, located: false)
                    : new SilTrap(Text(operands[0]).Value) { Site = site };
            }

            // The interpreter's frames are not the program's, and it has no
            // symbols to name them by: a backtrace here has none.
            case "backtrace_capture":
                return Allocate(new HeapArray(new ArrayType(SilType.String), []));

            // An error main threw: a trap, but the program ends with status 1.
            case "trap_uncaught": {
                var site = Text(operands[1]).Value;
                throw new SilTrap(Text(operands[0]).Value) { Site = site.Length == 0 ? null : site, ExitStatus = 1 };
            }

            // The panic hook, held as the runtime holds it: retained until
            // replaced, taken, or the program ends.
            case "trap_hook_set": {
                var hook = (ClosureValue)operands[0]!;
                Retain(builtin.Operands[0].Type, hook);
                var replaced = _trapHook;
                _trapHook = hook;
                if (replaced is not null) {
                    Release(builtin.Operands[0].Type, replaced, depth);
                }

                return null;
            }

            case "trap_hook_take": {
                var taken = _trapHook;
                _trapHook = null;
                if (taken is null) {
                    taken = (ClosureValue)operands[0]!;
                    Retain(builtin.Operands[0].Type, taken);
                }

                return taken;
            }

            // Nothing debugs the interpreted program, as a debugger traces a process.
            case "debugger_attached":
                return false;

            case "debugger_break":
                return null;

            case "io_succeeded":
                return !_ioFailed;

            case "io_read_line": {
                var line = Input.ReadLine();
                _ioFailed = line is null;
                return Allocate(new HeapString(line ?? ""));
            }

            case "io_read_file": {
                string? contents = null;
                try {
                    // Bytes, decoded as the runtime decodes them: no byte-order
                    // mark taken as a request for UTF-16, and ill-formed UTF-8 as U+FFFD.
                    contents = DecodeUtf8(File.ReadAllBytes(PathFor(operands[0], SilAccess.Read)));
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
                }

                _ioFailed = contents is null;
                return Allocate(new HeapString(contents ?? ""));
            }

            case "io_write_file": {
                try {
                    var (path, text) = (PathFor(operands[0], SilAccess.Write), Text(operands[1]).Value);
                    if ((bool)operands[2]!) {
                        File.AppendAllText(path, text);
                    } else {
                        File.WriteAllText(path, text);
                    }

                    return true;
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
                    return false;
                }
            }

            case "io_file_exists": {
                var path = PathFor(operands[0], SilAccess.Read);
                return File.Exists(path) || Directory.Exists(path);
            }

            case "io_is_directory":
                return Directory.Exists(PathFor(operands[0], SilAccess.Read));

            case "io_remove_file": {
                var path = PathFor(operands[0], SilAccess.Write);
                try {
                    if (File.Exists(path)) {
                        File.Delete(path);
                        return true;
                    }

                    if (Directory.Exists(path)) {
                        Directory.Delete(path, recursive: false);
                        return true;
                    }
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                }

                return false;
            }

            case "io_make_directory": {
                var path = PathFor(operands[0], SilAccess.Write);
                if (Directory.Exists(path) || File.Exists(path) || Path.GetDirectoryName(Path.GetFullPath(path)) is { } parent && !Directory.Exists(parent)) {
                    return false;
                }

                try {
                    Directory.CreateDirectory(path);
                    return true;
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    return false;
                }
            }

            case "io_list_directory": {
                var path = PathFor(operands[0], SilAccess.Read);
                string[] names;
                try {
                    names = [..Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Select(static n => n!)];
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
                    names = [];
                    _ioFailed = true;
                    return Allocate(new HeapArray(new ArrayType(SilType.String), []));
                }

                // Byte by byte, as the native runtime sorts them.
                Array.Sort(names, static (a, b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));
                _ioFailed = false;
                return Allocate(new HeapArray(new ArrayType(SilType.String), [..names.Select(n => (object?)Allocate(new HeapString(n)))]));
            }

            case "io_environment": {
                var name = Text(operands[0]).Value;
                var value = VariableOf(name);
                _ioFailed = value is null;
                return Allocate(new HeapString(value ?? ""));
            }

            case "process_status":
                return _lastStatus;

            case "process_error_output":
                return Allocate(new HeapString(_lastErrorOutput));

            case "process_current_directory":
                return Allocate(new HeapString(Sandbox?.CurrentDirectory ?? Directory.GetCurrentDirectory()));

            case "process_notify":
                return Sandbox is null ? Notify(Text(operands[0]).Value, Text(operands[1]).Value) : 1L;

            case "process_change_directory":
                try {
                    // A sandboxed program's working directory is its own: the
                    // process's stays where it is, for the rest of the build.
                    if (Sandbox is { } within) {
                        var directory = within.Check(Text(operands[0]).Value, SilAccess.Read);
                        if (!Directory.Exists(directory)) {
                            return false;
                        }

                        within.CurrentDirectory = directory;
                        return true;
                    }

                    Directory.SetCurrentDirectory(Text(operands[0]).Value);
                    return true;
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
                    return false;
                }

            case "memory_allocations":
                return (long)Allocations;

            case "process_id":
                return (long)Environment.ProcessId;

            // No executable runs here: an interpreted program cannot start itself again.
            case "process_executable_path":
                return Allocate(new HeapString(""));

            // .NET has one such clock for both: CLOCK_UPTIME_RAW, CLOCK_MONOTONIC or the performance counter.
            case "time_monotonic" or "time_suspending":
                return System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000L / System.Diagnostics.Stopwatch.Frequency);

            case "time_wall_clock":
                return (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100;

            case "time_sleep": {
                var nanoseconds = (long)operands[0]!;
                if (nanoseconds > 0) {
                    // Rounded up: never shorter than asked.
                    Thread.Sleep(TimeSpan.FromTicks((nanoseconds + 99) / 100));
                }

                return null;
            }

            case "io_argument_count":
                return (long)Arguments.Count;

            case "io_argument_bytes": {
                var index = (long)operands[0]!;
                if (index < 0 || index >= Arguments.Count) {
                    throw new SilTrap("argument index out of range");
                }

                return Allocate(new HeapArray(new ArrayType(SilType.U8), [..Encoding.UTF8.GetBytes(Arguments[(int)index]).Select(static b => (object?)(long)b)]));
            }

            case "env_set" or "env_remove": {
                // The program's own: written over what it reads, and what the
                // programs it runs are given, never the process's, which may
                // be the compiler's.
                var name = Text(operands[0]).Value;
                if (name.Length == 0 || name.Contains('=') || name.Contains('\0')) {
                    return false;
                }

                _environment[name] = builtin.Name == "env_set" ? Text(operands[1]).Value : null;
                return true;
            }

            case "env_all": {
                var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (Sandbox is { } within) {
                    foreach (var name in within.EnvironmentNames) {
                        if (within.Environment(name) is { } value) {
                            all[name] = value;
                        }
                    }
                } else {
                    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
                        all[(string)entry.Key] = (string?)entry.Value ?? "";
                    }
                }

                foreach (var (name, value) in _environment) {
                    if (value is null) {
                        all.Remove(name);
                    } else {
                        all[name] = value;
                    }
                }

                return Allocate(new HeapArray(new ArrayType(SilType.String), [..all.Select(e => (object?)Allocate(new HeapString(e.Key + "=" + e.Value)))]));
            }

            case "system_text":
                return Allocate(new HeapString((long)operands[0]! switch {
                    0 => Environment.MachineName,
                    1 => Environment.UserName,
                    2 => VariableOf(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    3 => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } temporary ? temporary : Path.GetTempPath(),
                    4 => Environment.OSVersion.Version.ToString(),
                    5 => UserLocale.Find(),
                    _ => "",
                }));

            case "system_number":
                return (long)operands[0]! switch {
                    0 => Environment.ProcessorCount,
                    1 => Environment.SystemPageSize,
                    2 => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    _ => 0L,
                };

            case "memory_statistic":
                return (long)operands[0]! switch {
                    0 => Allocations,
                    1 => _live.Count(static c => !c.Freed),
                    _ => 0L,
                };

            case "io_argument": {
                var index = (long)operands[0]!;
                if (index < 0 || index >= Arguments.Count) {
                    throw new SilTrap("argument index out of range");
                }

                return Allocate(new HeapString(Arguments[(int)index]));
            }

            case "string_from_Char":
                return Allocate(new HeapString(((Rune)operands[0]!).ToString()));

            case var _ when builtin.Name.StartsWith("string_from_", StringComparison.Ordinal) && builtin.Name != "string_from_Char":
                return Allocate(new HeapString(Format(operands[0])));

            case "string_to_Int": {
                var text = Text(operands[0]).Value;
                return IsInteger(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                    ? new object?[] { true, value }
                    : new object?[] { false, 0L };
            }

            case "string_to_F64": {
                var text = Encoding.UTF8.GetString(ByteRange(operands, 0));
                // Too large for an F64 does not fit, as for toInt.
                return IsDecimal(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && double.IsFinite(value)
                    ? new object?[] { true, value }
                    : new object?[] { false, 0.0 };
            }

            // To the nearest float in one rounding, as strtof does, not through a double.
            case "string_to_F32": {
                var text = Encoding.UTF8.GetString(ByteRange(operands, 0));
                return IsDecimal(text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && float.IsFinite(value)
                    ? new object?[] { true, value }
                    : new object?[] { false, 0f };
            }

            case "cmp_eq_String":
                return Text(operands[0]).Value == Text(operands[1]).Value;

            case "cmp_ne_String":
                return Text(operands[0]).Value != Text(operands[1]).Value;

            case "char_to_Int":
                return (long)((Rune)operands[0]!).Value;

            case "char_from_Int": {
                var code = (long)operands[0]!;
                return code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                    ? new object?[] { true, new Rune((int)code) }
                    : new object?[] { false, new Rune(0) };
            }

            case "char_is_digit":
                return (Rune)operands[0]! is { Value: >= '0' and <= '9' };

            case "array_count":
                return (long)ElementsOf(operands[0]).Count;

            case "array_mismatch": {
                if (!SilBuiltins.ComparesByBits(ArrayType.ElementOf(builtin.Operands[0].Type)!)) {
                    return -1L;
                }

                var (first, firstStart, firstCount) = ElementsOf(operands[0]);
                var (second, secondStart, secondCount) = ElementsOf(operands[1]);
                var shorter = Math.Min(firstCount, secondCount);
                var at = 0;
                while (at < shorter && Equals(first!.Elements[firstStart + at], second!.Elements[secondStart + at])) {
                    at++;
                }

                return (long)at;
            }

            // An array's element, or a slice's — checked against the slice's
            // count, not its buffer's.
            case "array_get" or "array_get_borrowed": {
                var (array, start, count) = ElementsOf(operands[0]);
                var index = (long)operands[1]!;
                if (array is null || index < 0 || index >= count) {
                    throw new SilTrap("array index out of range");
                }

                var element = array.Elements[start + (int)index];
                if (builtin.Name == "array_get") {
                    Retain(builtin.Type, element);
                }

                return element;
            }

            case "array_append":
                Unique(builtin.Operands[0], (IReference)operands[0]!).Elements.Add(operands[1]);
                return null;

            case "array_make_unique":
                if (((IReference)operands[0]!).Peek() is HeapArray) {
                    Unique(builtin.Operands[0], (IReference)operands[0]!);
                } else if (((IReference)operands[0]!).Peek() is SliceValue) {
                    UniqueSlice(builtin.Operands[0], (IReference)operands[0]!);
                }

                return null;

            case "array_set" or "array_set_unique": {
                if (builtin.Name == "array_set_unique"
                    && ((IReference)operands[0]!).Peek() is HeapArray { Strong: > 1 } or SliceValue { Buffer.Strong: > 1 }) {
                    throw new SilTrap("array_set_unique on a shared array");
                }

                var (array, at) = Writable(builtin.Operands[0], (IReference)operands[0]!, (long)operands[1]!);
                var old = array.Elements[at];
                array.Elements[at] = operands[2];
                Release(array.Type.Element, old, depth);
                return null;
            }

            case "array_element_addr" or "array_element_addr_unique": {
                var (array, at) = Writable(builtin.Operands[0], (IReference)operands[0]!, (long)operands[1]!);
                return new ArrayElement(array, at);
            }

            case "array_remove_last": {
                var array = Unique(builtin.Operands[0], (IReference)operands[0]!);
                if (array.Elements.Count == 0) {
                    throw new SilTrap("removeLast of an empty array");
                }

                var last = array.Elements[^1];
                array.Elements.RemoveAt(array.Elements.Count - 1);
                return last;
            }

            // Elements [from, to) of an array or a slice: a view of the same
            // buffer, which it holds a count of; nothing is copied.
            case "array_slice": {
                var (buffer, start, count) = ElementsOf(operands[0]);
                var (from, to) = ((long)operands[1]!, (long)operands[2]!);
                if (from < 0 || to > count || from > to) {
                    throw new SilTrap("array slice out of range");
                }

                if (from == to || buffer is null) {
                    return SliceValue.Empty;
                }

                Retain(buffer.Type, buffer);
                return new SliceValue(buffer, start + (int)from, (int)(to - from));
            }

            // A slice as an array again: the buffer itself when the slice is
            // the whole of it, else a copy of its run.
            // The view moved back into the array: its buffer as it is when it
            // is the whole of it, else a copy of its run, the view let go.
            case "array_of_slice_take": {
                var (buffer, start, count) = ElementsOf(operands[0]);
                if (buffer is null) {
                    return null;
                }

                if (start == 0 && count == buffer.Elements.Count) {
                    return buffer;
                }

                var copy = count == 0 ? null : Allocate(new HeapArray(buffer.Type, [..buffer.Elements.Skip(start).Take(count)]));
                foreach (var element in copy?.Elements ?? []) {
                    Retain(buffer.Type.Element, element);
                }

                Release(builtin.Operands[0].Type, operands[0], depth);
                return copy;
            }

            case "array_of_slice": {
                var (buffer, start, count) = ElementsOf(operands[0]);
                if (buffer is null || count == 0) {
                    return null;
                }

                if (start == 0 && count == buffer.Elements.Count) {
                    Retain(buffer.Type, buffer);
                    return buffer;
                }

                var copy = Allocate(new HeapArray(buffer.Type, [..buffer.Elements.Skip(start).Take(count)]));
                foreach (var element in copy.Elements) {
                    Retain(buffer.Type.Element, element);
                }

                return copy;
            }

            case "array_rotate": {
                var address = (IReference)operands[0]!;
                var (from, to, by) = ((long)operands[1]!, (long)operands[2]!, (long)operands[3]!);
                var (_, _, count) = ElementsOf(address.Peek());
                if (from < 0 || to > count || from > to) {
                    throw new SilTrap("array slice out of range");
                }

                var length = (int)(to - from);
                var shift = length == 0 ? 0 : (int)(((by % length) + length) % length);
                if (shift == 0) {
                    return null;
                }

                // Made the only reference to the elements first, as a write is.
                HeapArray buffer;
                var start = 0;
                if (address.Peek() is SliceValue) {
                    var unique = UniqueSlice(builtin.Operands[0], address);
                    (buffer, start) = (unique.Buffer!, unique.Start);
                } else {
                    buffer = Unique(builtin.Operands[0], address);
                }

                var at = start + (int)from;
                var moved = buffer.Elements.GetRange(at, shift);
                buffer.Elements.RemoveRange(at, shift);
                buffer.Elements.InsertRange(at + length - shift, moved);
                return null;
            }

            // A slice's elements stored over [from, to) of an array or a
            // slice: nothing when it is still the view of that range.
            case "array_store_slice": {
                var address = (IReference)operands[0]!;
                var (from, to) = ((long)operands[1]!, (long)operands[2]!);
                var (destination, destinationStart, destinationCount) = ElementsOf(address.Peek());
                if (from < 0 || to > destinationCount || from > to) {
                    throw new SilTrap("array slice out of range");
                }

                var (source, sourceStart, sourceCount) = ElementsOf(operands[3]);
                if (sourceCount != to - from) {
                    throw new SilTrap($"a slice of count {sourceCount} stored over a range of count {to - from}");
                }

                if (sourceCount == 0 || ReferenceEquals(source, destination) && sourceStart == destinationStart + from) {
                    return null;
                }

                var values = source!.Elements.GetRange(sourceStart, sourceCount);
                HeapArray buffer;
                var start = 0;
                if (address.Peek() is SliceValue) {
                    var unique = UniqueSlice(builtin.Operands[0], address);
                    (buffer, start) = (unique.Buffer!, unique.Start);
                } else {
                    buffer = Unique(builtin.Operands[0], address);
                }

                for (var i = 0; i < values.Count; i++) {
                    var at = start + (int)from + i;
                    var old = buffer.Elements[at];
                    Retain(buffer.Type.Element, values[i]);
                    buffer.Elements[at] = values[i];
                    Release(buffer.Type.Element, old, depth);
                }

                return null;
            }

            case "array_capacity":
                return operands[0] is HeapArray held ? (long)Alive(held).Elements.Capacity : 0L;

            case "array_reserve": {
                var minimum = (long)operands[1]!;
                if (minimum > 0) {
                    Unique(builtin.Operands[0], (IReference)operands[0]!).Elements.EnsureCapacity((int)minimum);
                }

                return null;
            }

            case "array_append_all": {
                var (source, from, count) = ElementsOf(operands[1]);
                if (count > 0) {
                    var element = ((SliceType)builtin.Operands[1].Type).Element;
                    var added = source!.Elements.GetRange(from, count);
                    var array = Unique(builtin.Operands[0], (IReference)operands[0]!);
                    array.Elements.EnsureCapacity(array.Elements.Count + count);
                    foreach (var value in added) {
                        Retain(element, value);
                        array.Elements.Add(value);
                    }
                }

                return null;
            }

            case "array_append_repeating": {
                var count = (long)operands[2]!;
                if (count < 0) {
                    throw new SilTrap("cannot append a negative number of elements");
                }

                if (count > int.MaxValue) {
                    throw new SilTrap("cannot append so many elements");
                }

                if (count > 0) {
                    var array = Unique(builtin.Operands[0], (IReference)operands[0]!);
                    var element = builtin.Operands[1].Type;
                    array.Elements.EnsureCapacity(array.Elements.Count + (int)count);
                    for (var i = 0; i < count; i++) {
                        Retain(element, operands[1]);
                        array.Elements.Add(operands[1]);
                    }
                }

                return null;
            }

            // A hole is a null, which a release or a copy passes over.
            case "array_append_holes": {
                var count = (long)operands[1]!;
                if (count < 0) {
                    throw new SilTrap("cannot append a negative number of elements");
                }

                if (count > int.MaxValue) {
                    throw new SilTrap("cannot append so many elements");
                }

                if (count > 0) {
                    Unique(builtin.Operands[0], (IReference)operands[0]!).Elements.AddRange(Enumerable.Repeat<object?>(null, (int)count));
                }

                return null;
            }

            case "array_take": {
                var (array, at) = Writable(builtin.Operands[0], (IReference)operands[0]!, (long)operands[1]!);
                var taken = array.Elements[at];
                if (!array.Type.Element.IsTrivial) {
                    array.Elements[at] = null;
                }

                return taken;
            }

            case "array_append_utf8": {
                var all = System.Text.Encoding.UTF8.GetBytes(Text(operands[1]).Value);
                var from = (long)operands[2]!;
                var to = (long)operands[3]!;
                if (from < 0 || to < from || to > all.Length) {
                    throw new SilTrap("UTF-8 offsets out of range");
                }

                var bytes = all.AsSpan((int)from, (int)(to - from)).ToArray();
                if (bytes.Length > 0) {
                    var array = Unique(builtin.Operands[0], (IReference)operands[0]!);
                    array.Elements.AddRange(bytes.Select(static b => (object?)(long)b));
                }

                return null;
            }

            case "array_truncate": {
                var count = (long)operands[1]!;
                var address = (IReference)operands[0]!;
                if (address.Peek() is null) {
                    return count == 0 ? null : throw new SilTrap("array index out of range");
                }

                var array = Unique(builtin.Operands[0], address);
                if (count < 0 || count > array.Elements.Count) {
                    throw new SilTrap("array index out of range");
                }

                for (var i = array.Elements.Count - 1; i >= count; i--) {
                    Release(array.Type.Element, array.Elements[i], depth);
                }

                array.Elements.RemoveRange((int)count, array.Elements.Count - (int)count);
                return null;
            }

            case "array_clear": {
                var address = (IReference)operands[0]!;
                var old = address.Peek();
                address.Set(null);
                Release(((AddressType)builtin.Operands[0].Type).Pointee, old, depth);
                return null;
            }
        }

        if (builtin.Name.EndsWith("_Char", StringComparison.Ordinal) && operands is [Rune left, Rune right]) {
            return Builtin(builtin.Name.Replace("_Char", "_Int", StringComparison.Ordinal), [(long)left.Value, (long)right.Value]);
        }

        if (builtin.Name.StartsWith("inline_", StringComparison.Ordinal)) {
            return Inline(builtin, operands, depth);
        }

        if (builtin.Name.StartsWith("task_", StringComparison.Ordinal) || builtin.Name.StartsWith("async_", StringComparison.Ordinal)) {
            return Task(builtin.Name, operands, depth);
        }

        if (builtin.Name.StartsWith("net_", StringComparison.Ordinal)) {
            return Net(builtin.Name, operands);
        }

        if (builtin.Name.StartsWith("process_", StringComparison.Ordinal)) {
            return Processes(builtin.Name, operands);
        }

        if (builtin.Name.StartsWith("signal_", StringComparison.Ordinal)) {
            return Signals(builtin.Name, operands);
        }

        if (builtin.Name.StartsWith("fs_", StringComparison.Ordinal)) {
            return FileSystem(builtin.Name, operands);
        }

        if (builtin.Name.StartsWith("file_", StringComparison.Ordinal)) {
            return Files(builtin.Name, operands);
        }

        if (MetadataBuiltin(builtin, operands, depth, out var answer)) {
            return answer;
        }

        if (builtin.Name.StartsWith("class_conforms:", StringComparison.Ordinal)) {
            var dynamic = Alive((HeapObject)operands[0]!).Type;
            return builtin.Name["class_conforms:".Length..].Split('&').All(protocol => {
                for (var at = dynamic; at is not null; at = at.Superclass) {
                    if (at.Conformances.Contains(protocol)) {
                        return true;
                    }
                }

                return false;
            });
        }

        if (builtin.Name.StartsWith("class_is:", StringComparison.Ordinal)) {
            var target = module.FindType(builtin.Name["class_is:".Length..]) as ClassType ?? throw new SilTrap($"no class {builtin.Name}");
            return Alive((HeapObject)operands[0]!).Type.IsSubclassOf(target);
        }

        if (builtin.Name.StartsWith("raw_", StringComparison.Ordinal)) {
            return RawBuiltin(builtin, operands);
        }

        if (builtin.Name.StartsWith("atomic_", StringComparison.Ordinal)) {
            return AtomicBuiltin(builtin, operands);
        }

        if (builtin.Name.StartsWith("lock_", StringComparison.Ordinal) || builtin.Name.StartsWith("rwlock_", StringComparison.Ordinal) || builtin.Name.StartsWith("condition_", StringComparison.Ordinal)) {
            return LockBuiltin(builtin, operands);
        }

        if (builtin.Name.StartsWith("access_", StringComparison.Ordinal)) {
            Access(builtin.Name, (IReference)operands[0]!);
            return null;
        }

        if (SimdBuiltins.Is(builtin.Name)) {
            return Simd(builtin, operands);
        }

        if (builtin.Name.StartsWith("bytes_load_", StringComparison.Ordinal)) {
            var width = BytesWidth(builtin.Type);
            var (buffer, start, count) = ElementsOf(operands[0]);
            var at = (long)operands[1]!;
            if (buffer is null || at < 0 || at > count - width) {
                throw new SilTrap("array index out of range");
            }

            ulong value = 0;
            for (var i = 0; i < width; i++) {
                value |= (ulong)(long)buffer.Elements[start + (int)at + i]! << (8 * i);
            }

            return width == 8 ? value : (long)value;
        }

        if (builtin.Name.StartsWith("bytes_store_", StringComparison.Ordinal)) {
            // The last byte's index checked, and the array made unique, as a
            // write of that byte would; then every byte written.
            var width = BytesWidth(builtin.Operands[2].Type);
            var at = (long)operands[1]!;
            if (at < 0 || at > long.MaxValue - width) {
                throw new SilTrap("array index out of range");
            }

            var (array, last) = Writable(builtin.Operands[0], (IReference)operands[0]!, at + width - 1);
            var value = operands[2] is ulong wide ? wide : (ulong)(long)operands[2]!;
            for (var i = 0; i < width; i++) {
                array.Elements[last - width + 1 + i] = (long)(byte)(value >> (8 * i));
            }

            return null;
        }

        if (builtin.Name is "bytes_random" or "bytes_secure_zero") {
            var (_, _, count) = ElementsOf(((IReference)operands[0]!).Peek());
            if (count > 0) {
                var random = new byte[count];
                if (builtin.Name == "bytes_random") {
                    System.Security.Cryptography.RandomNumberGenerator.Fill(random);
                }

                var (array, first) = Writable(builtin.Operands[0], (IReference)operands[0]!, 0);
                for (var i = 0; i < count; i++) {
                    array.Elements[first + i] = (long)random[i];
                }
            }

            return null;
        }

        if (FloatFunctions.Find(builtin.Name) is var (function, type)) {
            return Libm.Call(function, type, operands);
        }

        return NumericIntrinsic(builtin.Name, operands) ?? ByteOrder(builtin.Name, operands) ?? Numeric(builtin.Name, operands) ?? Builtin(builtin.Name, operands);
    }

    /// <summary>
    ///     The numeric protocols' builtins (SilBuiltins.IsNumericIntrinsic), as
    ///     the native code computes them: an integer's through BigInteger at
    ///     its width, a float's in .NET's IEEE operations of its own width.
    /// </summary>
    static object? NumericIntrinsic(string name, object?[] operands) {
        if (name.Split('_') is ["wrap" or "convert", var from, var to] && from == to) {
            return operands[0];
        }

        if (!SilBuiltins.IsNumericIntrinsic(name)) {
            return null;
        }

        var separator = name.LastIndexOf('_');
        var op = name[..separator];
        var type = SilType.Numbers.First(n => n.ToString() == name[(separator + 1)..]);
        if (SilType.FloatBits(type) is { } floatBits) {
            return FloatIntrinsic(op, type, floatBits, operands);
        }

        var (bits, signed) = SilType.IntegerInfo(type)!.Value;
        var x = Big(operands[0]);
        var pattern = Wrapped(x, bits, false);
        object Typed(BigInteger value) => NarrowBig(Wrapped(value, bits, signed), type);
        var (low, high) = signed
            ? (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1)
            : (BigInteger.Zero, (BigInteger.One << bits) - 1);

        switch (op) {
            case "count_leading_zeros":
                return (long)(bits - (pattern.IsZero ? 0 : (int)pattern.GetBitLength()));
            case "count_trailing_zeros": {
                if (pattern.IsZero) {
                    return (long)bits;
                }

                var count = 0L;
                while (pattern.IsEven) {
                    pattern >>= 1;
                    count++;
                }

                return count;
            }
            case "count_ones": {
                var count = 0L;
                for (; !pattern.IsZero; pattern >>= 1) {
                    count += (long)(pattern & 1);
                }

                return count;
            }
            case "byte_swap" or "bit_reverse": {
                var unit = op == "byte_swap" ? 8 : 1;
                var reversed = BigInteger.Zero;
                for (var i = 0; i < bits; i += unit) {
                    reversed = (reversed << unit) | ((pattern >> i) & ((BigInteger.One << unit) - 1));
                }

                return Typed(reversed);
            }
            case "rotate_left" or "rotate_right": {
                var count = (int)((((long)operands[1]! % bits) + bits) % bits);
                if (op == "rotate_right") {
                    count = (bits - count) % bits;
                }

                return Typed((pattern << count) | (pattern >> (bits - count)));
            }
            case "add_overflow" or "sub_overflow" or "mul_overflow": {
                var y = Big(operands[1]);
                var exact = op[..3] switch { "add" => x + y, "sub" => x - y, _ => x * y };
                return new object?[] { Typed(exact), exact < low || exact > high };
            }
            case "add_saturating" or "sub_saturating": {
                var y = Big(operands[1]);
                var exact = op[..3] == "add" ? x + y : x - y;
                return NarrowBig(BigInteger.Clamp(exact, low, high), type);
            }
            case "mul_high":
                return Typed((x * Big(operands[1])) >> bits);
            default:
                return null;
        }
    }

    /// <summary>A float's intrinsic, at its own width: F16 through the float or double that holds its every value exactly.</summary>
    static object? FloatIntrinsic(string op, SilType type, int bits, object?[] operands) {
        var x = Real(operands[0]);
        var y = operands.Length > 1 && op != "scale" ? Real(operands[1]) : 0.0;
        object Of(double value) => RealOf(value, type);
        switch (op) {
            case "sqrt": return Of(Math.Sqrt(x));
            case "floor": return Of(Math.Floor(x));
            case "ceil": return Of(Math.Ceiling(x));
            case "trunc": return Of(Math.Truncate(x));
            case "round": return Of(Math.Round(x, MidpointRounding.AwayFromZero));
            case "round_even": return Of(Math.Round(x, MidpointRounding.ToEven));
            case "copysign": return Of(Math.CopySign(x, y));
            case "minimum": return Of(Math.Min(x, y));
            case "maximum": return Of(Math.Max(x, y));
            case "remainder": return Of(Math.IEEERemainder(x, y));

            // An F16's product is exact in a double and so is the sum, which
            // is then rounded once; an F32's needs its own fused operation.
            case "fma":
                return type == SilType.F32
                    ? MathF.FusedMultiplyAdd((float)operands[0]!, (float)operands[1]!, (float)operands[2]!)
                    : Of(Math.FusedMultiplyAdd(x, y, Real(operands[2])));

            case "scale": return Of(Math.ScaleB(x, (int)Math.Clamp((long)operands[1]!, -100000, 100000)));
            case "exponent": return x == 0 ? long.MinValue : double.IsFinite(x) ? Math.ILogB(x) : long.MaxValue;
            case "next_up":
                return operands[0] switch {
                    Half h => Half.BitIncrement(h),
                    BFloat16 b => b.IsNaN || b.Bits == 0x7F80 ? b
                        : b.Bits == 0x8000 ? new BFloat16(0x0001)
                        : (b.Bits & 0x8000) != 0 ? new BFloat16((ushort)(b.Bits - 1)) : new BFloat16((ushort)(b.Bits + 1)),
                    float f => MathF.BitIncrement(f),
                    _ => Math.BitIncrement(x),
                };
            default:
                return null;
        }
    }

    /// <summary>How many bytes a 'bytes_load_' or 'bytes_store_' reads or writes.</summary>
    static int BytesWidth(SilType type) => SilType.IntegerInfo(type)!.Value.Bits / 8;

    /// <summary>
    ///     An integer's bytes reversed, and a float's bits as an integer and
    ///     back, on the values as the interpreter holds them (see <see cref="Numeric" />).
    /// </summary>
    static object? ByteOrder(string name, object?[] operands) =>
        (name, operands) switch {
            ("bitcast_F64_U64", [double x]) => BitConverter.DoubleToUInt64Bits(x),
            ("bitcast_U64_F64", [ulong x]) => BitConverter.UInt64BitsToDouble(x),
            ("bitcast_F32_U32", [float x]) => (long)BitConverter.SingleToUInt32Bits(x),
            ("bitcast_U32_F32", [long x]) => BitConverter.UInt32BitsToSingle((uint)x),
            ("bitcast_F16_U16", [Half x]) => (long)BitConverter.HalfToUInt16Bits(x),
            ("bitcast_U16_F16", [long x]) => BitConverter.UInt16BitsToHalf((ushort)x),
            ("bitcast_BF16_U16", [BFloat16 x]) => (long)x.Bits,
            ("bitcast_U16_BF16", [long x]) => new BFloat16((ushort)x),
            _ => null,
        };

    /// <summary>
    ///     The sized integers, F32 and F16, as the native code computes them:
    ///     through Int128 (BigInteger for the 128-bit types), trapping where
    ///     the result does not fit. A U64 or UInt is held as a ulong, an I128
    ///     as an Int128, a U128 as a UInt128, any other integer as a long; an
    ///     F32 as a float and an F16 as a Half.
    /// </summary>
    static object? Numeric(string name, object?[] operands) {
        static SilType? Find(string typeName) => SilType.Numbers.FirstOrDefault(n => n.ToString() == typeName);

        if (name.StartsWith("convert_", StringComparison.Ordinal)) {
            var parts = name.Split('_');
            if (name is "convert_Int_F64" or "convert_F64_Int" || Find(parts[1]) is not { } from || Find(parts[2]) is not { } to) {
                return null;
            }

            return ConvertNumber(operands[0], from, to);
        }

        if (name.StartsWith("wrap_", StringComparison.Ordinal)) {
            var parts = name.Split('_');
            return Find(parts[1]) is { } from && Find(parts[2]) is { } to ? WrapNumber(operands[0], from, to) : null;
        }

        if (name.StartsWith(SilBuiltins.WrappingPrefix, StringComparison.Ordinal)) {
            return Wrapping(name, operands);
        }

        var separator = name.LastIndexOf('_');
        if (separator < 0 || Find(name[(separator + 1)..]) is not { } type || type == SilType.Int || type == SilType.F64) {
            return null;
        }

        var op = name[..separator];
        if (type == SilType.F32 || type == SilType.F16 || type == SilType.BF16) {
            // An F16 or a BF16 is computed as a float and rounded back, which
            // gives the value each operation would: a float has room enough
            // that the two roundings never disagree.
            static float Single(object? value) => value switch { Half h => (float)h, BFloat16 b => b.ToSingle(), _ => (float)value! };
            var a = Single(operands[0]);
            var b = operands.Length > 1 ? Single(operands[1]) : 0f;
            object? Real(float value) => type == SilType.F16 ? (Half)value : type == SilType.BF16 ? BFloat16.FromSingle(value) : value;
            return op switch {
                "add" => Real(a + b),
                "sub" => Real(a - b),
                "mul" => Real(a * b),
                "div" => Real(a / b),
                "rem" => Real(a % b),
                "neg" => Real(-a),
                "abs" => Real(Math.Abs(a)),
                "min" => Real(float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Min(a, b)),
                "max" => Real(float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Max(a, b)),
                "cmp_eq" => a == b,
                "cmp_ne" => a != b,
                "cmp_lt" => a < b,
                "cmp_le" => a <= b,
                "cmp_gt" => a > b,
                "cmp_ge" => a >= b,
                _ => null,
            };
        }

        var (bits, signed) = SilType.IntegerInfo(type)!.Value;
        if (bits == 128) {
            return Numeric128(op, type, signed, operands);
        }

        var x = Wide(operands[0]);
        var y = operands.Length > 1 ? Wide(operands[1]) : Int128.Zero;
        object Fit(Int128 value) => InRange(value, bits, signed) ? Narrow(value, type) : throw new SilTrap("integer overflow");
        object Wrap(Int128 value) => Narrow(Wrapped(value, bits, signed), type);

        switch (op) {
            case "add": return Fit(x + y);
            case "sub": return Fit(x - y);
            case "mul": return Fit(x * y);
            case "neg": return Fit(-x);
            case "abs": return Fit(x < 0 ? -x : x);
            case "div" or "rem":
                return y == 0 ? throw new SilTrap("division by zero") : Fit(op == "div" ? x / y : x % y);
            case "shl" or "shr":
                return y < 0 || y >= bits ? throw new SilTrap("shift out of range") : Wrap(op == "shl" ? x << (int)y : x >> (int)y);
            case "and": return Wrap(x & y);
            case "or": return Wrap(x | y);
            case "xor": return Wrap(x ^ y);
            case "not": return Wrap(~x);
            case "min": return Narrow(x < y ? x : y, type);
            case "max": return Narrow(x < y ? y : x, type);
            case "cmp_eq": return x == y;
            case "cmp_ne": return x != y;
            case "cmp_lt": return x < y;
            case "cmp_le": return x <= y;
            case "cmp_gt": return x > y;
            case "cmp_ge": return x >= y;
            default: return null;
        }
    }

    /// <summary>
    ///     <c>wrapping_add_I32</c> and the rest, which a build with
    ///     <c>overflow-checks = false</c> has in place of the trapping
    ///     operations: the exact result, keeping the type's low bits.
    /// </summary>
    static object? Wrapping(string name, object?[] operands) {
        var separator = name.LastIndexOf('_');
        var type = SilType.Integers.FirstOrDefault(n => n.ToString() == name[(separator + 1)..]);
        if (type is null || separator <= SilBuiltins.WrappingPrefix.Length) {
            return null;
        }

        var (bits, signed) = SilType.IntegerInfo(type)!.Value;
        var x = Big(operands[0]);
        var y = operands.Length > 1 ? Big(operands[1]) : BigInteger.Zero;
        BigInteger? exact = name[SilBuiltins.WrappingPrefix.Length..separator] switch {
            "add" => x + y,
            "sub" => x - y,
            "mul" => x * y,
            "neg" => -x,
            "abs" => BigInteger.Abs(x),
            _ => null,
        };
        return exact is { } value ? NarrowBig(Wrapped(value, bits, signed), type) : null;
    }

    /// <summary>An I128 or U128 operation: Numeric's, over BigInteger, which holds a U128's every value.</summary>
    static object? Numeric128(string op, SilType type, bool signed, object?[] operands) {
        var x = Big(operands[0]);
        var y = operands.Length > 1 ? Big(operands[1]) : BigInteger.Zero;
        object Fit(BigInteger value) => InRange(value, 128, signed) ? NarrowBig(value, type) : throw new SilTrap("integer overflow");
        object Wrap(BigInteger value) => NarrowBig(Wrapped(value, 128, signed), type);

        switch (op) {
            case "add": return Fit(x + y);
            case "sub": return Fit(x - y);
            case "mul": return Fit(x * y);
            case "neg": return Fit(-x);
            case "abs": return Fit(BigInteger.Abs(x));
            case "div" or "rem":
                return y.IsZero ? throw new SilTrap("division by zero") : Fit(op == "div" ? BigInteger.Divide(x, y) : BigInteger.Remainder(x, y));
            case "shl" or "shr":
                return y < 0 || y >= 128 ? throw new SilTrap("shift out of range") : Wrap(op == "shl" ? x << (int)y : x >> (int)y);
            case "and": return Wrap(x & y);
            case "or": return Wrap(x | y);
            case "xor": return Wrap(x ^ y);
            case "not": return Wrap(~x);
            case "min": return NarrowBig(BigInteger.Min(x, y), type);
            case "max": return NarrowBig(BigInteger.Max(x, y), type);
            case "cmp_eq": return x == y;
            case "cmp_ne": return x != y;
            case "cmp_lt": return x < y;
            case "cmp_le": return x <= y;
            case "cmp_gt": return x > y;
            case "cmp_ge": return x >= y;
            default: return null;
        }
    }

    static Int128 Wide(object? value) =>
        value switch {
            long l => l,
            ulong u => u,
            _ => throw new SilTrap($"not an integer: {value}"),
        };

    /// <summary>Any integer the interpreter holds, at full width.</summary>
    static BigInteger Big(object? value) =>
        value switch {
            long l => l,
            ulong u => u,
            Int128 i => (BigInteger)i,
            UInt128 u => (BigInteger)u,
            _ => throw new SilTrap($"not an integer: {value}"),
        };

    static bool InRange(BigInteger value, int bits, bool signed) =>
        signed
            ? value >= -(BigInteger.One << (bits - 1)) && value < (BigInteger.One << (bits - 1))
            : value >= 0 && value < (BigInteger.One << bits);

    static BigInteger Wrapped(BigInteger value, int bits, bool signed) {
        var modulus = BigInteger.One << bits;
        var reduced = ((value % modulus) + modulus) % modulus;
        return signed && reduced >= (BigInteger.One << (bits - 1)) ? reduced - modulus : reduced;
    }

    /// <summary>A value known to fit the type, held as the interpreter holds that type.</summary>
    static object NarrowBig(BigInteger value, SilType type) =>
        type == SilType.I128 ? (Int128)value
        : type == SilType.U128 ? (UInt128)value
        : type == SilType.U64 || type == SilType.UInt ? (object)(ulong)value
        : (long)value;

    /// <summary>A float of any width, as a double, which holds each exactly.</summary>
    static double Real(object? value) =>
        value switch {
            float f => f,
            Half h => (double)h,
            BFloat16 b => b.ToDouble(),
            _ => (double)value!,
        };

    /// <summary>A double as the float type it is converted to, rounded once.</summary>
    static object RealOf(double value, SilType type) =>
        type == SilType.F32 ? (float)value
        : type == SilType.F16 ? (Half)value
        : type == SilType.BF16 ? BFloat16.FromDouble(value)
        : value;

    static bool InRange(Int128 value, int bits, bool signed) =>
        signed
            ? value >= -(Int128.One << (bits - 1)) && value < (Int128.One << (bits - 1))
            : value >= 0 && value < (Int128.One << bits);

    static Int128 Wrapped(Int128 value, int bits, bool signed) {
        var modulus = Int128.One << bits;
        var reduced = ((value % modulus) + modulus) % modulus;
        return signed && reduced >= (Int128.One << (bits - 1)) ? reduced - modulus : reduced;
    }

    static object Narrow(Int128 value, SilType type) =>
        type == SilType.U64 || type == SilType.UInt ? (object)(ulong)value : (long)value;

    /// <summary>
    ///     <c>unchecked as</c>: an integer keeps its low bits, read with the
    ///     target's sign; a float to an integer goes toward zero and saturates
    ///     at the target's bounds, NaN giving zero. Nothing here traps.
    /// </summary>
    static object WrapNumber(object? value, SilType from, SilType to) {
        var fromInteger = SilType.IntegerInfo(from);
        var toInteger = SilType.IntegerInfo(to);

        if (fromInteger is not null && toInteger is var (bits, signed)) {
            return NarrowBig(Wrapped(Big(value), bits, signed), to);
        }

        if (fromInteger is null && toInteger is var (toBits, toSigned)) {
            var real = Real(value);
            var low = toSigned ? -(BigInteger.One << (toBits - 1)) : BigInteger.Zero;
            var high = toSigned ? (BigInteger.One << (toBits - 1)) - 1 : (BigInteger.One << toBits) - 1;
            var clamped = double.IsNaN(real) ? BigInteger.Zero
                : real <= (double)low ? low
                : real >= (double)high ? high
                : new BigInteger(Math.Truncate(real));
            return NarrowBig(clamped, to);
        }

        return ConvertNumber(value, from, to);
    }

    static object ConvertNumber(object? value, SilType from, SilType to) {
        var fromInteger = SilType.IntegerInfo(from);
        var toInteger = SilType.IntegerInfo(to);
        var failed = new SilTrap($"{from} does not fit in {to}");

        if (fromInteger is not null && toInteger is var (bits, signed)) {
            var wide = Big(value);
            return InRange(wide, bits, signed) ? NarrowBig(wide, to) : throw failed;
        }

        // An integer to a float rounds once, as the native conversion does:
        // to an F32 straight from the integer, to an F16 through the double,
        // which is exact for every integer an F16 does not overflow on.
        if (fromInteger is not null) {
            var wide = Big(value);
            return to == SilType.F32 ? (float)wide : RealOf((double)wide, to);
        }

        var real = Real(value);
        if (toInteger is var (toBits, toSigned)) {
            var (low, high) = toSigned
                ? (-Math.Pow(2, toBits - 1), Math.Pow(2, toBits - 1))
                : (0.0, Math.Pow(2, toBits));
            return real >= low && real < high ? NarrowBig(new BigInteger(Math.Truncate(real)), to) : throw failed;
        }

        return RealOf(real, to);
    }

    /// <summary>The array at an address, made unique — copied if it is shared — so that it can be changed.</summary>
    HeapArray Unique(SilValue addressValue, IReference address) {
        var type = (ArrayType)Concrete(((AddressType)addressValue.Type).Pointee);
        if (address.Peek() is not HeapArray array) {
            var fresh = Allocate(new HeapArray(type, []));
            address.Set(fresh);
            return fresh;
        }

        if (Alive(array).Strong == 1) {
            return array;
        }

        var copy = Allocate(new HeapArray(type, [..array.Elements]));
        foreach (var element in copy.Elements) {
            Retain(type.Element, element);
        }

        array.Strong--;
        Releases++;
        address.Set(copy);
        return copy;
    }

    /// <summary>
    ///     Where element <paramref name="index" /> of the array or slice at an
    ///     address is, to change: the buffer made the only reference to the
    ///     elements first, and the index checked against the count.
    /// </summary>
    (HeapArray Array, int At) Writable(SilValue addressValue, IReference address, long index) {
        if (address.Peek() is SliceValue view) {
            if (index < 0 || index >= view.Count) {
                throw new SilTrap("array index out of range");
            }

            var unique = UniqueSlice(addressValue, address);
            return (unique.Buffer!, unique.Start + (int)index);
        }

        var array = Unique(addressValue, address);
        if (index < 0 || index >= array.Elements.Count) {
            throw new SilTrap("array index out of range");
        }

        return (array, (int)index);
    }

    /// <summary>
    ///     The slice at an address, made the only reference to its buffer: a
    ///     shared one gives way to a copy of the slice's run alone, as an
    ///     array's copy is of its own elements.
    /// </summary>
    SliceValue UniqueSlice(SilValue addressValue, IReference address) {
        var view = (SliceValue)address.Peek()!;
        if (view.Buffer is not { } buffer || Alive(buffer).Strong == 1) {
            return view;
        }

        // The buffer's own type: an opaque element's is the type it is (#297).
        var type = buffer.Type;
        var copy = Allocate(new HeapArray(type, [..buffer.Elements.Skip(view.Start).Take(view.Count)]));
        foreach (var element in copy.Elements) {
            Retain(type.Element, element);
        }

        buffer.Strong--;
        Releases++;
        var unique = new SliceValue(copy, 0, view.Count);
        address.Set(unique);
        return unique;
    }

    /// <summary>The whitespace the runtime knows: ASCII's.</summary>
    /// <summary>Unicode's White_Space (PropList.txt, 16.0), as the runtime's trimming has it.</summary>
    static bool IsWhitespace(Rune c) =>
        c.Value is >= 0x09 and <= 0x0D or 0x20 or 0x85 or 0xA0 or 0x1680 or >= 0x2000 and <= 0x200A or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;

    static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

    /// <summary>Whether the byte offset is where a scalar starts, or the end.</summary>
    static bool IsBoundary(byte[] bytes, long at) => at >= 0 && at <= bytes.Length && (at == bytes.Length || !IsContinuation(bytes[at]));

    /// <summary>
    ///     The bytes a range builtin's string and two offsets, from the
    ///     operand at <paramref name="first" /> on, name: a Substring's, which
    ///     the library has checked lie inside the string.
    /// </summary>
    ReadOnlySpan<byte> ByteRange(object?[] operands, int first) {
        var from = (int)(long)operands[first + 1]!;
        var to = (int)(long)operands[first + 2]!;
        return Text(operands[first]).Utf8.AsSpan(from, to - from);
    }

    /// <summary>The offset, checked to be where a scalar starts, as a String.Index must be to read there.</summary>
    static int ScalarStart(byte[] bytes, long at) {
        if (at < 0 || at >= bytes.Length) {
            throw new SilTrap("string index out of range");
        }

        return IsContinuation(bytes[at]) ? throw new SilTrap("string index not on a character boundary") : (int)at;
    }

    /// <summary>The scalar whose well-formed UTF-8 starts at the offset, and how many bytes it takes.</summary>
    static (Rune Scalar, int Width) DecodeUtf8(byte[] bytes, int at) {
        Rune.DecodeFromUtf8(bytes.AsSpan(at), out var scalar, out var width);
        return (scalar, width);
    }

    /// <summary>A slice of bytes, or an array of them, as the bytes.</summary>
    static byte[] BytesOf(object? value) {
        var (buffer, start, count) = ElementsOf(value);
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) {
            bytes[i] = (byte)(long)buffer!.Elements[start + i]!;
        }

        return bytes;
    }

    /// <summary>
    ///     The seed every Hasher starts from: random per process, as the
    ///     runtime's <c>sable_hash_seed</c> is, or the runtime's fixed one with
    ///     <c>SABLE_DETERMINISTIC_HASHING=1</c>, so that a program then prints
    ///     the same hashes natively and interpreted.
    /// </summary>
    internal static readonly long HashSeed =
        Environment.GetEnvironmentVariable("SABLE_DETERMINISTIC_HASHING") == "1"
            ? 0x243F6A8885A308D3
            : BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    /// <summary>
    ///     A string's UTF-8 bytes hashed under a key, sixteen to a folded
    ///     multiply: the runtime's <c>sable_string_hash</c> exactly, so a
    ///     program hashes alike natively and interpreted.
    /// </summary>
    internal static long StringHash(byte[] bytes, long key) {
        unchecked {
            static ulong Fold(ulong a, ulong b) {
                var high = Math.BigMul(a, b, out var low);
                return low ^ high;
            }

            ulong Load64(int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
            ulong Load32(int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

            var k0 = (ulong)key;
            var k1 = ulong.RotateLeft(k0, 32) ^ 0x9E3779B97F4A7C15;
            var n = bytes.Length;
            var acc = k0 ^ ((ulong)n * 0xD6E8FEB86659FD93);
            if (n <= 16) {
                ulong a = 0, b = 0;
                if (n >= 8) {
                    a = Load64(0);
                    b = Load64(n - 8);
                } else if (n >= 4) {
                    a = Load32(0);
                    b = Load32(n - 4);
                } else if (n > 0) {
                    a = bytes[0];
                    b = ((ulong)bytes[n / 2] << 8) | bytes[n - 1];
                }

                acc = Fold(a ^ acc, b ^ k1);
            } else {
                var i = 0;
                for (; i + 16 < n; i += 16) {
                    acc = Fold(Load64(i) ^ acc, Load64(i + 8) ^ k1);
                }

                acc = Fold(Load64(n - 16) ^ acc, Load64(n - 8) ^ k1);
            }

            return (long)Fold(acc, k1 ^ 0x2D358DCCAA6C78A5);
        }
    }

    /// <summary>An optional sign and decimal digits: what <c>toInt</c> reads, and nothing else.</summary>
    static bool IsInteger(string text) {
        var i = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        return i < text.Length && text[i..].All(static c => c is >= '0' and <= '9');
    }

    /// <summary>A decimal number: sign, digits, a fraction, an exponent. No infinities, NaN or hex.</summary>
    public static bool IsDecimal(string text) {
        var i = 0;
        if (i < text.Length && text[i] is '+' or '-') {
            i++;
        }

        var digits = 0;
        while (i < text.Length && text[i] is >= '0' and <= '9') {
            i++;
            digits++;
        }

        if (i < text.Length && text[i] == '.') {
            i++;
            while (i < text.Length && text[i] is >= '0' and <= '9') {
                i++;
                digits++;
            }
        }

        if (digits == 0) {
            return false;
        }

        if (i < text.Length && text[i] is 'e' or 'E') {
            i++;
            if (i < text.Length && text[i] is '+' or '-') {
                i++;
            }

            var exponent = 0;
            while (i < text.Length && text[i] is >= '0' and <= '9') {
                i++;
                exponent++;
            }

            if (exponent == 0) {
                return false;
            }
        }

        return i == text.Length;
    }

    static object Builtin(string name, object?[] operands) {
        checked {
            return (name, operands) switch {
                ("add_Int", [long a, long b]) => Wrap(() => a + b),
                ("wrapping_add_Int", [long a, long b]) => unchecked(a + b),
                ("wrapping_sub_Int", [long a, long b]) => unchecked(a - b),
                ("wrapping_mul_Int", [long a, long b]) => unchecked(a * b),
                ("overflow_at_add_Int", [long a, long b, long i, long m]) => Overflows(() => a + b) ? Math.Min(m, i) : m,
                ("overflow_at_sub_Int", [long a, long b, long i, long m]) => Overflows(() => a - b) ? Math.Min(m, i) : m,
                ("overflow_at_mul_Int", [long a, long b, long i, long m]) => Overflows(() => a * b) ? Math.Min(m, i) : m,
                ("sub_Int", [long a, long b]) => Wrap(() => a - b),
                ("mul_Int", [long a, long b]) => Wrap(() => a * b),
                ("div_Int", [long a, long b]) => b == 0 ? throw new SilTrap("division by zero") : Wrap(() => a / b),
                ("rem_Int", [long a, long b]) => b == 0 ? throw new SilTrap("division by zero") : Wrap(() => a % b),
                ("select_Int", [bool c, long a, long b]) => c ? a : b,
                ("and_Int", [long a, long b]) => a & b,
                ("or_Int", [long a, long b]) => a | b,
                ("xor_Int", [long a, long b]) => a ^ b,
                ("shl_Int", [long a, long b]) => b is < 0 or > 63 ? throw new SilTrap("shift out of range") : a << (int)b,
                ("shr_Int", [long a, long b]) => b is < 0 or > 63 ? throw new SilTrap("shift out of range") : a >> (int)b,
                ("neg_Int", [long a]) => Wrap(() => -a),
                ("not_Int", [long a]) => ~a,
                ("abs_Int", [long a]) => Wrap(() => Math.Abs(a)),
                ("min_Int", [long a, long b]) => Math.Min(a, b),
                ("max_Int", [long a, long b]) => Math.Max(a, b),

                ("add_F64", [double a, double b]) => a + b,
                ("sub_F64", [double a, double b]) => a - b,
                ("mul_F64", [double a, double b]) => a * b,
                ("div_F64", [double a, double b]) => a / b,
                ("rem_F64", [double a, double b]) => a % b, // C#'s '%' on doubles is fmod
                ("neg_F64", [double a]) => -a,
                ("abs_F64", [double a]) => Math.Abs(a),

                // C's fmin and fmax: a NaN operand gives the other one.
                ("min_F64", [double a, double b]) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Min(a, b),
                ("max_F64", [double a, double b]) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b),

                ("and_Bool", [bool a, bool b]) => a & b,
                ("or_Bool", [bool a, bool b]) => a | b,
                ("xor_Bool", [bool a, bool b]) => a ^ b,
                ("not_Bool", [bool a]) => !a,

                ("cmp_eq_Int", [long a, long b]) => a == b,
                ("cmp_ne_Int", [long a, long b]) => a != b,
                ("cmp_lt_Int", [long a, long b]) => a < b,
                ("cmp_le_Int", [long a, long b]) => a <= b,
                ("cmp_gt_Int", [long a, long b]) => a > b,
                ("cmp_ge_Int", [long a, long b]) => a >= b,

                ("cmp_eq_F64", [double a, double b]) => a == b,
                ("cmp_ne_F64", [double a, double b]) => a != b,
                ("cmp_lt_F64", [double a, double b]) => a < b,
                ("cmp_le_F64", [double a, double b]) => a <= b,
                ("cmp_gt_F64", [double a, double b]) => a > b,
                ("cmp_ge_F64", [double a, double b]) => a >= b,

                ("cmp_eq_Bool", [bool a, bool b]) => a == b,
                ("cmp_ne_Bool", [bool a, bool b]) => a != b,

                ("convert_Int_F64", [long a]) => (double)a,
                ("convert_F64_Int", [double a]) => a is >= -9.2233720368547758e18 and < 9.2233720368547758e18
                    ? (long)a
                    : throw new SilTrap("F64 does not fit in Int"),

                _ => throw new SilTrap($"builtin \"{name}\" cannot take these operands"),
            };
        }
    }

    /// <summary>
    ///     A printed value, exactly as the native runtime writes it: integers in
    ///     decimal, Booleans as words, and floating point with its point kept.
    /// </summary>
    public static string Format(object? value) =>
        value switch {
            long n => n.ToString(CultureInfo.InvariantCulture),
            ulong n => n.ToString(CultureInfo.InvariantCulture),
            Int128 n => n.ToString(CultureInfo.InvariantCulture),
            UInt128 n => n.ToString(CultureInfo.InvariantCulture),
            float f => FormatFloat(f),
            Half h => FormatHalf(h),
            BFloat16 b => FormatBFloat16(b),
            bool b => b ? "true" : "false",
            double d => FormatDouble(d),
            string s => s,
            HeapString s => Alive(s).Value,
            Rune c => c.ToString(),
            _ => "?",
        };

    /// <summary>
    ///     The shortest digits that read back as the same value — <c>0.1</c>,
    ///     not <c>0.10000000000000001</c> — laid out as the runtime writes them.
    /// </summary>
    static string FormatFloat(float value) {
        if (Special(value) is { } special) {
            return special;
        }

        var scientific = "";
        for (var precision = 1; precision <= 9; precision++) {
            scientific = ((double)value).ToString("E" + (precision - 1), CultureInfo.InvariantCulture);
            if (float.Parse(scientific, NumberStyles.Float, CultureInfo.InvariantCulture) == value) {
                break;
            }
        }

        return Layout(scientific);
    }

    /// <summary>An F16 the same way, against a half, as the runtime reads the digits back: through a float.</summary>
    static string FormatHalf(Half value) {
        if (Special((double)value) is { } special) {
            return special;
        }

        var scientific = "";
        for (var precision = 1; precision <= 5; precision++) {
            scientific = ((double)value).ToString("E" + (precision - 1), CultureInfo.InvariantCulture);
            if ((Half)float.Parse(scientific, NumberStyles.Float, CultureInfo.InvariantCulture) == value) {
                break;
            }
        }

        return Layout(scientific);
    }

    /// <summary>A BF16 the same way, against a bfloat16, as the runtime reads the digits back: through a float.</summary>
    static string FormatBFloat16(BFloat16 value) {
        if (Special(value.ToDouble()) is { } special) {
            return special;
        }

        var scientific = "";
        for (var precision = 1; precision <= 4; precision++) {
            scientific = value.ToDouble().ToString("E" + (precision - 1), CultureInfo.InvariantCulture);
            if (BFloat16.FromSingle(float.Parse(scientific, NumberStyles.Float, CultureInfo.InvariantCulture)) == value) {
                break;
            }
        }

        return Layout(scientific);
    }

    static string FormatDouble(double value) {
        if (Special(value) is { } special) {
            return special;
        }

        var scientific = "";
        for (var precision = 1; precision <= 17; precision++) {
            scientific = value.ToString("E" + (precision - 1), CultureInfo.InvariantCulture);
            if (double.Parse(scientific, NumberStyles.Float, CultureInfo.InvariantCulture) == value) {
                break;
            }
        }

        return Layout(scientific);
    }

    static string? Special(double value) =>
        double.IsNaN(value) ? "nan"
        : double.IsInfinity(value) ? value > 0 ? "inf" : "-inf"
        : null;

    /// <summary>
    ///     Scientific digits laid out as the runtime's <c>layout_float</c> and
    ///     Python's <c>repr</c> do: plain decimal with a point for exponents -4
    ///     through 15 (<c>100.0</c>, <c>-0.0</c>, <c>0.0001</c>), scientific
    ///     outside them (<c>1e+20</c>, <c>1.5e-07</c>). The point keeps a whole
    ///     float from reading as an integer.
    /// </summary>
    static string Layout(string scientific) {
        var negative = scientific.StartsWith('-');
        var e = scientific.IndexOf('E', StringComparison.Ordinal);
        var digits = scientific[(negative ? 1 : 0)..e].Replace(".", "", StringComparison.Ordinal).TrimEnd('0');
        if (digits.Length == 0) {
            digits = "0";
        }

        var exponent = int.Parse(scientific[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var text = new StringBuilder(negative ? "-" : "");
        if (exponent is < -4 or >= 16) {
            text.Append(digits[0]);
            if (digits.Length > 1) {
                text.Append('.').Append(digits, 1, digits.Length - 1);
            }

            text.Append('e').Append(exponent < 0 ? '-' : '+')
                .Append(Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture));
        } else if (exponent < 0) {
            text.Append("0.").Append('0', -exponent - 1).Append(digits);
        } else if (digits.Length > exponent + 1) {
            text.Append(digits, 0, exponent + 1).Append('.').Append(digits, exponent + 1, digits.Length - exponent - 1);
        } else {
            text.Append(digits).Append('0', exponent + 1 - digits.Length).Append(".0");
        }

        return text.ToString();
    }

    static bool Overflows(Func<long> operation) {
        try {
            _ = checked(operation());
            return false;
        } catch (OverflowException) {
            return true;
        }
    }

    static object Wrap(Func<long> operation) {
        try {
            return checked(operation());
        } catch (OverflowException) {
            throw new SilTrap("integer overflow");
        }
    }
}
