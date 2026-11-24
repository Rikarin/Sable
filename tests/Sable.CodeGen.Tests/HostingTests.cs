using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Sable.Symbols;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A host as a program of its own (Sable.Hosting): what it does at the
///     signals it is sent, how the program ends, and what it tells systemd.
///     Every signal goes to a program the test started. The library's own
///     tests (tests/lib/Sable.Hosting) say the rest.
/// </summary>
[Native]
public sealed class HostingTests {
    const int Hangup = 1, Interrupt = 2, Terminate = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    static extern int SendSignal(int process, int signal);

    static string Worker(string service, string setup = "") => $$"""
        module P
        import Sable.Concurrency
        import Sable.Hosting
        import Sable.Time

        @main
        async fn main() {
            let builder = ApplicationBuilder()
            {{setup}}
            builder.services.addWorker("worker") { context, cancellation in
                {{service}}
            }
            let host = try! builder.build()
            host.lifetime.onReload { Runtime.writeError("reloaded\n") }
            let _ = host.lifetime.started.register { Runtime.writeError("started\n") }
            let _ = host.lifetime.stopping.register { Runtime.writeError("stopping\n") }
            let _ = host.lifetime.stopped.register { Runtime.writeError("stopped\n") }
            await host.runToExit()
        }
        """;

    const string Waits = "let _ = await cancellation.cancellationRequested()";

    /// <summary>The program built, started, and driven by <paramref name="drive" />, which reads its output's lines; what it left of its output, its errors, and its status.</summary>
    static (string Output, string Errors, int Status) Drive(string source, Action<Process, Func<string?>> drive, IReadOnlyDictionary<string, string>? environment = null) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, false);
            Assert.True(build.Succeeded, build.Output);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
            start.Environment.Remove("NOTIFY_SOCKET");
            foreach (var (name, value) in environment ?? new Dictionary<string, string>()) {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            try {
                // Standard output is written out when its buffer is full, so
                // all is said on standard error: the program's, and the host's log.
                var unread = process.StandardOutput.ReadToEndAsync();
                var log = new StringBuilder();
                string? Line() {
                    while (true) {
                        var read = process.StandardError.ReadLineAsync();
                        Assert.True(read.Wait(TimeSpan.FromSeconds(30)), "the program wrote no more");
                        if (read.Result is not { } line || !line.Contains(" host: ", StringComparison.Ordinal)) {
                            return read.Result;
                        }

                        log.Append(line).Append('\n');
                    }
                }

                drive(process, Line);
                Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the program did not end");
                var rest = new StringBuilder();
                while (Line() is { } line) {
                    rest.Append(line).Append('\n');
                }

                return (rest.ToString(), log.ToString(), process.ExitCode);
            } finally {
                if (!process.HasExited) {
                    process.Kill();
                }
            }
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    static bool CanSignal => CanBuild && !OperatingSystem.IsWindows();

    [Fact]
    public void ATermSignalStopsItWithStatusZero() {
        if (!CanSignal) {
            return;
        }

        foreach (var signal in new[] { Terminate, Interrupt }) {
            var (output, errors, status) = Drive(Worker(Waits), (process, line) => {
                Assert.Equal("started", line());
                Assert.Equal(0, SendSignal(process.Id, signal));
            });
            Assert.Equal("stopping\nstopped\n", output);
            Assert.Equal(0, status);
            Assert.Contains($"INFO host: asked to stop signal={(signal == Terminate ? "TERM" : "INT")}", errors, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AHangupReloadsItAndItRunsOn() {
        if (!CanSignal) {
            return;
        }

        var (output, _, status) = Drive(Worker(Waits), (process, line) => {
            Assert.Equal("started", line());
            SendSignal(process.Id, Hangup);
            Assert.Equal("reloaded", line());
            SendSignal(process.Id, Hangup);
            Assert.Equal("reloaded", line());
            SendSignal(process.Id, Terminate);
        });
        Assert.Equal("stopping\nstopped\n", output);
        Assert.Equal(0, status);
    }

    /// <summary>A service deaf to everything, and no shutdown timeout to speak of: the second interrupt ends it.</summary>
    [Fact]
    public void ASecondInterruptEndsItAtOnce() {
        if (!CanSignal) {
            return;
        }

        var (output, errors, status) = Drive(Worker("while true { }", "builder.options.shutdownTimeout = Duration.hours(1)"), (process, line) => {
            Assert.Equal("started", line());
            SendSignal(process.Id, Interrupt);
            Assert.Equal("stopping", line());
            SendSignal(process.Id, Interrupt);
        });
        Assert.Equal("", output);
        Assert.Equal(130, status);
        Assert.Contains("interrupted while stopping", errors, StringComparison.Ordinal);
    }

    /// <summary>A service that never ends does not keep the program: past the timeout, it ends with status 1.</summary>
    [Fact]
    public void AServiceThatNeverEndsDoesNotKeepTheProgram() {
        if (!CanBuild) {
            return;
        }

        var (output, errors, status) = Drive(Worker("context.lifetime.requestStop()\nwhile true { }", "builder.options.shutdownTimeout = Duration.milliseconds(100)"), (_, _) => { });
        Assert.EndsWith("stopping\nstopped\nnot stopped in time: worker\n", output);
        Assert.Equal(1, status);
        Assert.Contains("ERROR host: not stopped in time services=worker", errors, StringComparison.Ordinal);
    }

    static string NotifyProgram => Worker(Waits, "builder.useSystemd()\n    builder.services.addWorker(\"told\") { _context, _cancellation in print(Systemd.notify(\"STATUS=Serving\")) }");

    static void Listen(string endpoint, UnixDomainSocketEndPoint address) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified) { ReceiveTimeout = 30_000 };
        socket.Bind(address);
        var buffer = new byte[4096];
        string Receive() => Encoding.UTF8.GetString(buffer, 0, socket.Receive(buffer));

        var (output, _, status) = Drive(NotifyProgram, (process, line) => {
            var told = new HashSet<string>();
            while (told.Count < 2) {
                if (Receive() is var first and not "WATCHDOG=1") {
                    told.Add(first);
                }
            }

            Assert.Equal(new HashSet<string> { "READY=1", "STATUS=Serving" }, told);
            // The watchdog, twice within its fifth of a second.
            Assert.Equal("WATCHDOG=1", Receive());
            Assert.Equal("WATCHDOG=1", Receive());
            SendSignal(process.Id, Hangup);
            string message;
            while ((message = Receive()) == "WATCHDOG=1") {
            }

            Assert.Matches(@"^RELOADING=1\nMONOTONIC_USEC=\d+$", message);
            while ((message = Receive()) == "WATCHDOG=1") {
            }

            Assert.Equal("READY=1", message);
            SendSignal(process.Id, Terminate);
            while ((message = Receive()) == "WATCHDOG=1") {
            }

            Assert.Equal("STOPPING=1", message);
        }, new Dictionary<string, string> { ["NOTIFY_SOCKET"] = endpoint, ["WATCHDOG_USEC"] = "200000" });
        Assert.EndsWith("stopped\n", output);
        Assert.Equal(0, status);
    }

    [Fact]
    public void SystemdIsToldOverItsSocket() {
        if (!CanSignal) {
            return;
        }

        // A short path: a socket's holds a hundred bytes or so.
        var path = Path.Combine("/tmp", $"sable-{Guid.NewGuid():N}"[..20]);
        try {
            Listen(path, new(path));
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void SystemdIsToldOverAnAbstractSocket() {
        if (!CanSignal || !OperatingSystem.IsLinux()) {
            return;
        }

        var name = $"sable-{Guid.NewGuid():N}";
        Listen("@" + name, new("\0" + name));
    }

    /// <summary>What the interpreter sends is what the runtime does.</summary>
    [Fact]
    public void ADatagramIsSentBothWays() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        var path = Path.Combine("/tmp", $"sable-{Guid.NewGuid():N}"[..20]);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified) { ReceiveTimeout = 10_000 };
        socket.Bind(new UnixDomainSocketEndPoint(path));
        try {
            Assert.Equal(
                "true false false\n",
                AssertAgree($$"""
                    module P
                    import Sable.Hosting

                    @main
                    fn main() {
                        let sent = Systemd.notify("READY=1\nSTATUS=žluťoučký", socket: "{{path}}")
                        print("\(sent) \(Systemd.notify("READY=1", socket: "{{path}}x")) \(Systemd.notify("READY=1", socket: "relative"))")
                    }
                    """).Stdout
            );
            var buffer = new byte[256];
            for (var i = 0; i < (CanBuild ? 2 : 1); i++) {
                Assert.Equal("READY=1\nSTATUS=žluťoučký", Encoding.UTF8.GetString(buffer, 0, socket.Receive(buffer)));
            }
        } finally {
            File.Delete(path);
        }
    }
}
