using Sable.Symbols;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable bench</c>: build the <c>@bench</c> functions, optimised, into a
///     harness that times each and compares with a saved baseline.
/// </summary>
public sealed class BenchCommand : HarnessCommand {
    public override string Name => "bench";
    public override string Summary => "run the @bench functions and compare with a baseline";

    public override string Usage => """
        usage: sable bench <file.sb>... [filter...] [--save-baseline <name>] [--baseline <name>] [--baseline-dir <path>] [--quick] [--samples <n>] [--list] [-O0]

          filter                  run the benchmarks whose names (Module.function) contain it
          --save-baseline <name>  keep the results, as <baseline-dir>/<name>.txt
          --baseline <name>       compare with a kept baseline (by default, with 'default' if kept)
          --baseline-dir <path>   where baselines are kept (default: target/bench)
          --quick                 a few short samples: to check the benchmarks run, not to time them
          --samples <n>           how many samples to time (default 20)
          --list                  list the benchmarks instead of running them
          -O0                     build without LLVM's optimiser
        """;

    protected override TestKind Kind => TestKind.Bench;
    protected override bool OptimiseByDefault => true;

    protected override IReadOnlyDictionary<string, bool> HarnessOptions { get; } = new Dictionary<string, bool>(StringComparer.Ordinal) {
        ["--list"] = false,
        ["--save-baseline"] = true,
        ["--baseline"] = true,
        ["--baseline-dir"] = true,
        ["--quick"] = false,
        ["--samples"] = true,
    };
}
