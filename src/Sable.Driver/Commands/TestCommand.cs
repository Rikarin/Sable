using System.Globalization;
using Sable.Symbols;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable test</c>: build the <c>@test</c> functions into a harness and
///     run them, each in a process of its own; exits 0 when all pass.
/// </summary>
public sealed class TestCommand : HarnessCommand {
    public override string Name => "test";
    public override string Summary => "run the @test functions (and, with --doc, the doc examples)";

    public override string Usage => """
        usage: sable test [<file.sb>...] [filter...] [--doc] [--bless] [--list] [--exact] [--show-output] [--release] [-g] [-- <harness options>]

        Without a file, tests the package in this directory or the nearest above it: its
        library with the files under tests/, and its dependencies.

          filter             run the tests whose names (Module.function) contain it
          --doc              also run the ```sable examples in doc comments, as tests
          --bless            write each generator snapshot case's expected/ from what the generators produce (docs/08 §8.8)
          --list             list the tests instead of running them
          --exact            a filter is a whole name, not part of one
          --show-output      show what passing tests print, too
          --release          build optimised (tests build without the optimiser by default)
          -g                 build with debug information
          --exclusivity=...  keep or drop the dynamic exclusivity checks (kept by default)
          -j, --jobs <n>     run n tests at once (default: one per processor)
          --timeout <secs>   stop a test that runs longer, and fail it (default: none; one running
                             over 60 s is said to be)
          --in-process       run the tests one after another in one process, the first failure ending it
          --harness-out <p>  build the harness to p, with '-g' to debug it ('<p> --run-test <name>'), and run nothing
        """;

    int _jobs;
    TimeSpan? _timeout;

    public override int Run(IReadOnlyList<string> arguments) {
        // The runner's own options; the rest are the harness's and the build's.
        _jobs = Environment.ProcessorCount;
        _timeout = null;
        var rest = new List<string>();
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "-j" or "--jobs" when i + 1 < arguments.Count && int.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out var jobs) && jobs > 0:
                    _jobs = jobs;
                    i++;
                    break;
                case "--timeout" when i + 1 < arguments.Count && double.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out var seconds) && seconds > 0:
                    _timeout = TimeSpan.FromSeconds(seconds);
                    i++;
                    break;
                case "-j" or "--jobs" or "--timeout":
                    return BadUsage();
                case "--":
                    rest.AddRange(arguments.Skip(i));
                    i = arguments.Count;
                    break;
                default:
                    rest.Add(arguments[i]);
                    break;
            }
        }

        return base.Run(rest);
    }

    /// <summary>
    ///     Runs the tests several at once, each in a process of its own and
    ///     within the time limit (<see cref="TestRunner" />); a listing, or a
    ///     run in one process, is the harness's own.
    /// </summary>
    protected override int Execute(string executable, IReadOnlyList<string> arguments) =>
        TestRunner.Handles(arguments)
            ? TestRunner.Run(executable, arguments, _jobs, _timeout, Console.Out, Console.Error)
            : base.Execute(executable, arguments);

    protected override TestKind Kind => TestKind.Test;
    protected override bool OptimiseByDefault => false;

    protected override IReadOnlyDictionary<string, bool> HarnessOptions { get; } = new Dictionary<string, bool>(StringComparer.Ordinal) {
        ["--list"] = false,
        ["--exact"] = false,
        ["--show-output"] = false,
        ["--in-process"] = false,
    };
}
