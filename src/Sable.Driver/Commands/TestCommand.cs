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
        usage: sable test <file.sb>... [filter...] [--doc] [--bless] [--list] [--exact] [--show-output] [--release] [-g] [-- <harness options>]

          filter             run the tests whose names (Module.function) contain it
          --doc              also run the ```sable examples in doc comments, as tests
          --bless            write each generator snapshot case's expected/ from what the generators produce (docs/08 §8.8)
          --list             list the tests instead of running them
          --exact            a filter is a whole name, not part of one
          --show-output      show what passing tests print, too
          --release          build optimised (tests build without the optimiser by default)
          -g                 build with debug information
          --exclusivity=...  keep or drop the dynamic exclusivity checks (kept by default)
        """;

    protected override TestKind Kind => TestKind.Test;
    protected override bool OptimiseByDefault => false;

    protected override IReadOnlyDictionary<string, bool> HarnessOptions { get; } = new Dictionary<string, bool>(StringComparer.Ordinal) {
        ["--list"] = false,
        ["--exact"] = false,
        ["--show-output"] = false,
        ["--in-process"] = false,
    };
}
