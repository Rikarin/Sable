namespace Sable.Symbols.Tests;

/// <summary>
///     <c>Task</c>, <c>Region.alloc</c>, <c>Decoded</c>, <c>Bytes.of</c> and
///     <c>InlineLayout</c>: declared in Sable (<c>lib/Sable.Core</c>), their
///     bodies the compiler's — <c>@intrinsic</c>, or, for <c>fromBytes</c>,
///     synthesised per <c>@inline_layout</c> struct.
/// </summary>
public sealed class CoreIntrinsicTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(params string[] sources) => [
        ..Compilation.Create(sources)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static Diagnostic Single(ErrorCode code, params string[] sources) {
        var errors = ErrorsIn(sources);
        Assert.True(errors.Count == 1, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        Assert.Equal(code, errors[0].Code);
        return errors[0];
    }

    static void Clean(params string[] sources) {
        var errors = ErrorsIn(sources);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void TheyAreDeclaredInSable() {
        var compilation = Compilation.Create("module M");
        var core = compilation.Core;

        Assert.NotNull(core.Task);
        Assert.NotNull(core.Decoded);
        Assert.NotNull(core.Bytes);
        Assert.NotNull(core.InlineLayout);

        foreach (var type in new[] { core.Task!, core.Decoded!, core.Bytes!, core.InlineLayout! }) {
            Assert.False(type.DeclaringSyntax.IsEmpty);
        }

        Assert.False(core.Region.GetMembers("alloc").Single().DeclaringSyntax.IsEmpty);
        Assert.True(core.Task!.GetMembers("value").OfType<FieldSymbol>().Single().IsAsyncGetter);
        Assert.Single(core.Task.GetMembers("yield"));
    }

    [Fact]
    public void TasksAreSpawnedAwaitedAndYieldedTo() =>
        Clean(
            """
            module M
            fn work() async -> Int { await Task.yield(); return 1 }
            fn main() async -> Int {
                let task = Task.spawn { await work() }
                return await task.value
            }
            """
        );

    [Fact]
    public void ATasksValueIsAwaited() {
        var error = Single(
            ErrorCode.ERR_MustUseAwait,
            """
            module M
            fn main() async -> Int {
                let task = Task.spawn { 1 }
                return task.value
            }
            """
        );
        Assert.Contains("'value'", error.Message);
    }

    [Fact]
    public void ATaskIsMadeBySpawnAlone() =>
        Single(
            ErrorCode.ERR_NotAccessible,
            """
            module M
            fn main() { let task = Task<Int>() }
            """
        );

    [Fact]
    public void AnAsyncGetterIsAwaitedAndMayAwait() {
        Clean(
            """
            module M
            class Counter {
                var n: Int = 1
                var later: Int {
                    async get {
                        await Task.yield()
                        return n
                    }
                }
            }
            fn main() async -> Int { return await Counter().later }
            """
        );

        Single(
            ErrorCode.ERR_MustUseAwait,
            """
            module M
            class Counter {
                var later: Int { async get { return 1 } }
            }
            fn main() async -> Int { return Counter().later }
            """
        );
    }

    [Fact]
    public void ARegionAllocatesAnyType() =>
        Clean(
            """
            module M
            struct Node { var value: Int }
            fn main() -> Int {
                region arena {
                    let node = arena.alloc(Node { value: 1 })
                    let label: String& = arena.alloc("one")
                    return node.value + label.count
                }
            }
            """
        );

    [Fact]
    public void AnInlineLayoutStructConformsAndDecodes() {
        var compilation = Compilation.Create(
            """
            module M
            @inline_layout
            struct Packed { var a: U8, var b: U8 }
            fn roundTrip(_ p: Packed) -> String {
                return match Packed.fromBytes(Bytes.of(p)) {
                    case .Ok(let back) => "\(back.a)"
                    case .Invalid(let why) => why
                }
            }
            fn generic<T: InlineLayout>(_ bytes: U8[>..]) -> Decoded<T> { return T.fromBytes(bytes) }
            """
        );
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);

        var packed = compilation.GetTypeByQualifiedName("M.Packed")!;
        Assert.Contains(compilation.Core.InlineLayout!, packed.BaseTypes);
        var fromBytes = packed.GetMembers("fromBytes").OfType<FunctionSymbol>().Single();
        Assert.True(fromBytes.IsStatic);
        Assert.Equal("Decoded<Packed>", fromBytes.ReturnType.ToDisplayString());
    }

    [Fact]
    public void BytesOfWantsAnInlineLayoutValue() {
        var error = Single(
            ErrorCode.ERR_NoMatchingOverload,
            """
            module M
            struct Plain { var a: U8 }
            fn main() -> Int { return Bytes.of(Plain { a: 1 }).count }
            """
        );
        Assert.Contains("InlineLayout", error.Message);
    }

    [Fact]
    public void InlineLayoutIsConformedToByTheAttributeAlone() {
        var error = Single(
            ErrorCode.ERR_DoesNotConform,
            """
            module M
            struct Plain: InlineLayout { var a: U8 }
            """
        );
        Assert.Contains("@inline_layout", error.Message);
    }

    [Fact]
    public void AnInlineLayoutStructCannotWriteItsOwnFromBytes() =>
        Single(
            ErrorCode.ERR_DuplicateDeclaration,
            """
            module M
            @inline_layout
            struct Packed {
                var a: U8
                static fn fromBytes(_ bytes: U8[>..]) -> Decoded<Packed> { return .Invalid(why: "mine") }
            }
            """
        );
}
