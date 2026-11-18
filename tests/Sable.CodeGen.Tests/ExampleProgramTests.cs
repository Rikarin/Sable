using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The showcase programs in <c>examples/</c>, each with a <c>main</c>,
///     compiled and run — natively and in the interpreter, which must agree —
///     and held to what they print. None of them needs a network.
/// </summary>
/// <remarks>
///     <c>02-shapes.sab</c> joins them once a <c>some P</c> parameter lowers;
///     until then <c>sable test</c> runs its <c>@test</c> through the same
///     pipeline, and <c>CorpusLoweringTests</c> holds the rest of it to the
///     verifier.
/// </remarks>
public sealed class ExampleProgramTests {
    [Fact]
    public void HelloGreetsEveryone() =>
        Assert.Equal(
            "hello, world\nhello, Sable\nhello, everyone\n",
            Run("01-hello.sab")
        );

    [Fact]
    public void TheMemoryTiersWorkTogether() =>
        Assert.Equal(
            """
            order 7 is 38 bytes, total 4498
            loaded order 7 for Ann, total 4498
            rejected order: bytes after the value
            the corrupt copy was refused
            1+2*3+4*5 = 27
            frame 1: notes.md
            drew 1
            notes.md belongs to a workspace: true
            removed tiers-output.txt
            removed tiers-input.txt
            copied: tiers
            closing notes.md
            closing plan.md

            """,
            Run("03-memory-tiers.sab")
        );

    [Fact]
    public void TheServiceFetchesCachesAndFails() =>
        Assert.Equal(
            """
            1: Ada
            2: Grace
            5: user 5 has no name
            6: malformed user payload
            7: shard 0 is down
            8: no such user
            fetched 4 of 5
            worker 2: 3: Barbara
            worker 1: 8: no such user
            worker 2: 9: no such user
            worker 1: 14: shard 0 is down
            served 4

            """,
            Run("04-async-service.sab")
        );

    static string Run(string example) =>
        AssertAgree(File.ReadAllText(Path.Combine(Root, "examples", example)), stepLimit: 50_000_000).Stdout;

    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }
}
