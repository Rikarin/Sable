using System.Globalization;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The I/O reactor (runtime/tasks.c, platform_posix.c's kqueue and epoll,
///     platform_win32.c's completion port, #276): a task waiting for a socket
///     suspends, and the worker that would have blocked runs others; the
///     parked worker waits in the reactor until the earliest sleeper is due,
///     so timers and sockets are served by one wait; and work that must block
///     runs on the blocking pool (Task.blocking). The interpreter agrees,
///     through Socket.Select.
/// </summary>
[Native]
public sealed class ReactorTests {
    static string AsyncProgram(string declarations, string body) =>
        $"module Test\nimport Sable.IO\nimport Sable.Time\n{declarations}\n@main\nfn main() async throws {{\n{body}\n}}\n";

    const string EchoServer = """
                              fn serve(_ listener: TcpListener, _ count: Int) async throws(IoError) -> Int {
                                  var served = 0
                                  for _ in 0..<count {
                                      let connection = try await listener.accept()
                                      let bytes = try await connection.readAll()
                                      var reply: U8[>..] = []
                                      for b in bytes {
                                          reply.append(b + 1)
                                      }
                                      try await connection.write(reply)
                                      connection.close()
                                      served += 1
                                  }
                                  return served
                              }

                              fn ask(_ port: Int, _ seed: U8, _ count: Int) async throws(IoError) -> Int {
                                  let connection = try await TcpStream.connect(host: "127.0.0.1", port: port)
                                  var bytes: U8[>..] = []
                                  for _ in 0..<count {
                                      bytes.append(seed)
                                  }
                                  try await connection.write(bytes)
                                  connection.finishWriting()
                                  let reply = try await connection.readAll()
                                  var total = 0
                                  for b in reply {
                                      total += Int(b)
                                  }
                                  return total
                              }
                              """;

    /// <summary>
    ///     A server task and four clients, each its own task, talking over
    ///     loopback: every accept, connect, read and write that must wait
    ///     suspends its task. The same on the pool, on one thread, and
    ///     interpreted.
    /// </summary>
    [Fact]
    public void TasksTalkOverSocketsWithoutBlockingAWorker() {
        var source = AsyncProgram(EchoServer, """
                let listener = try TcpListener.bind(host: "127.0.0.1", port: 0)
                let server = Task.spawn { try? await serve(listener, 4) }
                var clients: Task<Int?>[>..] = []
                for i in 0..<4 {
                    clients.append(Task.spawn { try? await ask(listener.port, U8(i * 10), 3) })
                }
                var sum = 0
                for c in clients {
                    sum += (await c.value) ?? -1
                }
                print("served \(await server.value ?? -1), sum \(sum)")
            """);
        Assert.Equal("served 4, sum 192\n", AssertAgree(source).Stdout);
        if (CanBuild) {
            Assert.Equal("served 4, sum 192\n", BuildAndRun(source, environment: OneThread).Stdout);
            Assert.Equal("served 4, sum 192\n", BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = "1" }).Stdout);
        }
    }

    /// <summary>
    ///     A megabyte through one connection: the writer fills the socket's
    ///     buffer and waits for the reader to drain it, again and again.
    /// </summary>
    [Fact]
    public void AWriterWaitsForASlowReader() {
        var source = AsyncProgram(EchoServer, """
                let listener = try TcpListener.bind(host: "127.0.0.1", port: 0)
                let server = Task.spawn { try? await serve(listener, 1) }
                let total = try await ask(listener.port, 1, 1_000_000)
                print("\(total) \(await server.value ?? -1)")
            """);
        Assert.Equal("2000000 1\n", CanBuild ? BuildAndRun(source).Stdout : Interpret(source).Output);
    }

    /// <summary>
    ///     A task waiting to accept a connection that never comes is woken by
    ///     its cancellation and throws; meanwhile a sleeper in the same
    ///     program wakes on time, the parked worker waiting in the reactor
    ///     no longer than its deadline.
    /// </summary>
    [Fact]
    public void ACancelledAcceptThrowsAndSleepersWakeMeanwhile() {
        var source = AsyncProgram("", """
                let listener = try TcpListener.bind(host: "127.0.0.1", port: 0)
                let waiting = Task.spawn { () -> String in
                    do {
                        let _ = try await listener.accept()
                        return "accepted"
                    } catch {
                        return error.description()
                    }
                }
                let start = Instant.now()
                try await Task.sleep(nanoseconds: 100_000_000)
                let slept = start.elapsed.totalNanoseconds / 1_000_000
                waiting.cancel()
                print(await waiting.value)
                print(slept >= 100 && slept < 1000)
            """);
        Assert.Equal("the task was cancelled while it waited\ntrue\n", AssertAgree(source).Stdout);
        if (CanBuild) {
            Assert.Equal("the task was cancelled while it waited\ntrue\n", BuildAndRun(source, environment: OneThread).Stdout);
        }
    }

    /// <summary>Nothing listening: connecting throws Unreachable, and closing a listener wakes its accept.</summary>
    [Fact]
    public void ConnectingToNothingThrowsAndClosingWakesAnAccept() {
        var source = AsyncProgram("", """
                let listener = try TcpListener.bind(host: "127.0.0.1", port: 0)
                let port = listener.port
                let waiting = Task.spawn { () -> String in
                    do {
                        let _ = try await listener.accept()
                        return "accepted"
                    } catch {
                        return error.description()
                    }
                }
                await Task.yield()
                listener.close()
                print(await waiting.value)
                do {
                    let _ = try await TcpStream.connect(host: "127.0.0.1", port: port)
                    print("connected")
                } catch {
                    print(error.description() == "'127.0.0.1:\(port)' could not be reached")
                }
            """);
        Assert.Equal("the connection failed or was closed\ntrue\n", AssertAgree(source).Stdout);
    }

    /// <summary>
    ///     Work that blocks runs on the blocking pool: on one worker, four
    ///     tasks each blocking a thread for 200 ms take about 200 ms, not
    ///     800, and the worker runs a fifth task meanwhile.
    /// </summary>
    [Fact]
    [Trait("Category", "Timing")]
    public void BlockingWorkRunsOffTheWorkers() {
        if (!CanBuild) {
            return;
        }

        var source = AsyncProgram("", """
                let start = Instant.now()
                var tasks: Task<Int>[>..] = []
                for i in 0..<4 {
                    tasks.append(Task.spawn { await Task.blocking { () -> Int in
                        Runtime.sleep(nanoseconds: 200_000_000)
                        return i
                    } })
                }
                let ticker = Task.spawn { () -> Int in
                    var ticks = 0
                    for _ in 0..<10 {
                        try? await Task.sleep(nanoseconds: 10_000_000)
                        ticks += 1
                    }
                    return ticks
                }
                var sum = 0
                for t in tasks {
                    sum += await t.value
                }
                print(sum)
                print(await ticker.value)
                print(start.elapsed.totalNanoseconds / 1_000_000)
            """);
        var lines = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = "1" }).Stdout.Split('\n');
        Assert.Equal("6", lines[0]);
        Assert.Equal("10", lines[1]);
        var milliseconds = long.Parse(lines[2], CultureInfo.InvariantCulture);
        Assert.True(milliseconds < 600, $"{milliseconds} ms");
    }

    /// <summary>
    ///     Task.blocking gives what its body gives, or throws what it throws —
    ///     a file read whole on the blocking pool, say — natively and
    ///     interpreted, where it runs at once.
    /// </summary>
    [Fact]
    public void BlockingGivesWhatItsBodyGivesOrThrows() =>
        Assert.Equal(
            "42\nhello\nnothing is at '/no/such/file'\n",
            AssertAgree(AsyncProgram("", """
                    let n = await Task.blocking { 6 * 7 }
                    print(n)
                    let text = await Task.blocking { () -> String in "hel" + "lo" }
                    print(text)
                    do {
                        let read = try await Task.blocking { () throws(IoError) -> String in try File.read("/no/such/file") }
                        print(read)
                    } catch {
                        print(error.description())
                    }
                """)).Stdout);
}
