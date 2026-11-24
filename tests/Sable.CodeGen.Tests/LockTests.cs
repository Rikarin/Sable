using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Concurrency's locks: the interpreter and the native code agree
///     where nothing waits, and the interpreter, one thread running every
///     task, traps on a wait no other thread could end — where the native
///     program would hang.
/// </summary>
[Native]
public sealed class LockTests {
    [Fact]
    public void TheInterpreterAndTheNativeCodeAgree() {
        var run = AssertAgree("""
            module P
            import Sable.Concurrency
            @main
            fn main() async {
                let names: Mutex<String[>..]> = Mutex([])
                names.withLock { $0.append("a") }
                print(names.withLock { $0.count })
                print(names.withLockIfAvailable { $0[0] } ?? "-")
                let table = RwLock<Int>(1)
                table.write { $0 += 1 }
                print(table.read { $0 })
                let slots = Semaphore(permits: 1)
                print(slots.tryAcquire())
                print(slots.tryAcquire())
                slots.release()
                let once = Once()
                once.run { print("once") }
                once.run { print("twice") }
                let gate = AsyncMutex()
                print(await gate.withLock { 7 })
                let permits = AsyncSemaphore(permits: 1)
                await permits.acquire()
                print(await permits.tryAcquire())
            }
            """);
        Assert.Equal("1\na\n2\ntrue\nfalse\nonce\n7\nfalse\n", run.Stdout);
    }

    [Fact]
    public void TheInterpreterTrapsOnALockTakenTwice() {
        var (_, trap) = Interpret("""
            module P
            import Sable.Concurrency
            @main
            fn main() {
                let n = Mutex<Int>(0)
                n.withLock { value in
                    value = n.withLock { $0 + 1 }
                }
            }
            """);
        Assert.NotNull(trap);
        Assert.Contains("held already", trap, StringComparison.Ordinal);
    }
}
