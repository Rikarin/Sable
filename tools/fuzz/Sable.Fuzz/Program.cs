using System.Runtime.InteropServices;
using System.Text;
using Sable.Fuzz;

// Coverage-guided fuzzing of the compiler's C# parts. tools/fuzz/coverage.sh
// is the way to run it: it publishes this project, rewrites the Sable
// assemblies with SharpFuzz so that each records the branches it takes, and
// runs every target under libFuzzer where there is one (Linux, through
// libfuzzer-dotnet) or under the loop in this process where there is not
// (macOS), keeping and minimising what fails. By hand, from the published
// directory, with the assemblies instrumented:
//
//     Sable.Fuzz <target>                   under libfuzzer-dotnet (--target_path=Sable.Fuzz --target_arg=<target>)
//     Sable.Fuzz <target> fuzz <seconds> <corpus> <artifacts> [<seeds>...]
//                                           the in-process coverage-guided loop
//     Sable.Fuzz <target> replay <file>...  each input once: what fails, and its signature
//     Sable.Fuzz <target> minimise <file> <out> [<seconds>]
//                                           the smallest input that fails the same way
//     Sable.Fuzz <target> seeds <dir>       the corpus's first inputs, from the repository
//     Sable.Fuzz <target> dict <file>       the target's tokens, as a libFuzzer dictionary
//     Sable.Fuzz <target> info              its longest input and its time limit in seconds
//     Sable.Fuzz <target> stability <file>  how steady an input's coverage is, run six times: the
//                                           first run's extra edges are one-time set-up
//
// The targets are parser, editor, frontend, sil, resolver, packages and interface
// (Targets.cs, MoreTargets.cs, InterfaceTarget.cs). Run
// from the repository's root, or anywhere under it. Exit codes: 0 when all is
// well, 1 when an input failed, 3 when one hung (the in-process loop stops
// there), 2 for a usage mistake.

if (args.Length == 0 || Target.Named(args[0]) is not { } target) {
    Console.Error.WriteLine($"usage: Sable.Fuzz <{string.Join("|", Target.All.Select(static t => t.Name))}> [fuzz|replay|minimise|seeds|dict|info|stability] ...");
    return 2;
}

if (Environment.GetEnvironmentVariable("__LIBFUZZER_SHM_ID") is not null) {
    return LibFuzzer.Run(target);
}

// Every instrumented branch writes to SharpFuzz's map: there has to be one,
// whether or not anything reads it.
unsafe {
    SharpFuzz.Common.Trace.SharedMem = (byte*)NativeMemory.AllocZeroed(1 << 16);
}

// Ctrl-C or SIGTERM: what is running is dropped, and the loop says how far it got.
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

var root = Root();
var command = args.Length > 1 ? args[1] : "";
switch (command) {
    case "fuzz" when args.Length >= 5 && int.TryParse(args[2], out var seconds): {
        var dictionary = target.Dictionary().Select(Encoding.UTF8.GetBytes).ToList();
        var loop = new CoverageLoop(target, args[3], args[5..], args[4], dictionary, Environment.TickCount);
        return loop.Run(TimeSpan.FromSeconds(seconds));
    }
    case "replay" when args.Length >= 3: {
        using var runner = new Runner(target);
        var failed = false;
        foreach (var file in args[2..]) {
            var input = File.ReadAllBytes(file);
            switch (runner.Run(input)) {
                case (Runner.Outcome.Failed, { } failure):
                    Console.WriteLine($"{file}: fails");
                    Console.WriteLine(Runner.Report(failure, input));
                    failed = true;
                    break;
                case (Runner.Outcome.Hung, _):
                    Console.WriteLine($"{file}: hangs\nsignature: timeout: no answer within {target.Limit.TotalSeconds}s");
                    return 3;
                case (Runner.Outcome.Stopped, _):
                    return 1;
                default:
                    Console.WriteLine($"{file}: passes");
                    break;
            }
        }

        return failed ? 1 : 0;
    }
    case "minimise" when args.Length >= 4: {
        using var runner = new Runner(target);
        var input = File.ReadAllBytes(args[2]);
        if (runner.Run(input) is not (Runner.Outcome.Failed, { } failure)) {
            Console.WriteLine($"{args[2]}: does not fail here, so there is nothing to minimise");
            return 0;
        }

        var budget = TimeSpan.FromSeconds(args.Length > 4 && int.TryParse(args[4], out var s) ? s : 120);
        var smallest = runner.Minimise(input, Runner.Signature(failure), budget);
        File.WriteAllBytes(args[3], smallest);
        Console.WriteLine($"{args[2]}: {input.Length} bytes minimised to {smallest.Length}, in {args[3]}");
        return 0;
    }
    case "stability" when args.Length >= 3: {
        // A target whose coverage wanders from run to run keeps inputs for noise.
        using var runner = new Runner(target);
        var input = File.ReadAllBytes(args[2]);
        byte[]? first = null;
        for (var i = 0; i < 6; i++) {
            unsafe {
                NativeMemory.Clear(SharpFuzz.Common.Trace.SharedMem, 1 << 16);
                SharpFuzz.Common.Trace.PrevLocation = 0;
                runner.Run(input);
                var map = new ReadOnlySpan<byte>(SharpFuzz.Common.Trace.SharedMem, 1 << 16).ToArray();
                if (first is null) {
                    first = map;
                    Console.WriteLine($"run 0: {map.Count(static b => b != 0)} edges");
                } else {
                    Console.WriteLine($"run {i}: {map.Count(static b => b != 0)} edges, {Enumerable.Range(0, map.Length).Count(j => (map[j] == 0) != (first[j] == 0))} differ from run 0 in being taken, {Enumerable.Range(0, map.Length).Count(j => map[j] != first[j])} in count");
                }
            }
        }

        return 0;
    }
    case "info":
        // What tools/fuzz/coverage.sh tells libFuzzer: the longest input, and the seconds one may take.
        Console.WriteLine($"{target.MaxLength} {(int)target.Limit.TotalSeconds}");
        return 0;
    case "seeds" when args.Length >= 3: {
        Directory.CreateDirectory(args[2]);
        var count = 0;
        foreach (var (name, data) in target.Seeds(root)) {
            File.WriteAllBytes(Path.Combine(args[2], name), data.Length > target.MaxLength ? data[..target.MaxLength] : data);
            count++;
        }

        Console.WriteLine($"{target.Name}: {count} seeds in {args[2]}");
        return 0;
    }
    case "dict" when args.Length >= 3: {
        // libFuzzer's format: one quoted token a line, with \\, \" and \xNN escapes.
        var text = new StringBuilder($"# {target.Name}: generated by Sable.Fuzz from the lexer's tables and the target's own tokens\n");
        foreach (var token in target.Dictionary()) {
            text.Append('"');
            foreach (var b in Encoding.UTF8.GetBytes(token)) {
                text.Append(b switch {
                    (byte)'\\' => "\\\\",
                    (byte)'"' => "\\\"",
                    < 0x20 or >= 0x7f => $"\\x{b:X2}",
                    _ => ((char)b).ToString(),
                });
            }

            text.Append("\"\n");
        }

        File.WriteAllText(args[2], text.ToString());
        return 0;
    }
    default:
        Console.Error.WriteLine($"usage: Sable.Fuzz {target.Name} [fuzz <seconds> <corpus> <artifacts> [<seeds>...] | replay <file>... | minimise <file> <out> [<seconds>] | seeds <dir> | dict <file> | info | stability <file>]");
        return 2;
}

static void Stop(PosixSignalContext context) {
    context.Cancel = true;
    Runner.Stopping = true;
}

static string Root() {
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("run Sable.Fuzz from the repository, where Sable.slnx is");
}
