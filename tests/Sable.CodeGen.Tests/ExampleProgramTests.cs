using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The showcase programs in <c>examples/</c>, each with a <c>main</c>,
///     compiled and run — natively and in the interpreter, which must agree —
///     and held to what they print. None of them needs a network
///     but the loopback interface.
/// </summary>
[Native]
public sealed class ExampleProgramTests {
    [Fact]
    public void HelloGreetsEveryone() =>
        Assert.Equal(
            "hello, world\nhello, Sable\nhello, everyone\n",
            Run("01-hello.sb")
        );

    [Fact]
    public void ShapesMeasureThemselves() =>
        Assert.Equal(
            """
            Geometry: area 12.566370614359172
            Geometry: area 12.0
            Geometry: area 6.0
            Geometry: area 4.141592653589793
            total area 34.70796326794897
            total perimeter 48.84955592153876
            Square: area 4.0
            Geometry: area 3.141592653589793
            Square: area 1.0
            Square: area 2.25, perimeter 6.0
            Geometry: area 7.0, perimeter 11.0
            42 within 200 is 42.0
            42.5% of 200 is 85.0

            """,
            Run("02-shapes.sb")
        );

    [Fact]
    public void TheMemoryTiersWorkTogether() =>
        Assert.Equal(
            """
            order 7 is 38 bytes, total 4498
            loaded order 7 for Ann, total 4498
            rejected order: bytes after the value
            the corrupt copy was refused
            1+2*3+4*5 = 27.0
            frame 1: notes.md
            drew 1
            notes.md belongs to a workspace: true
            removed tiers-output.txt
            removed tiers-input.txt
            copied: tiers
            closing notes.md
            closing plan.md

            """,
            Run("03-memory-tiers.sb")
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
            Run("04-async-service.sb", OneThread)
        );

    [Fact]
    public void TheWorkerStartsShipsAndStops() =>
        Assert.Equal(
            """
            intake: open
            shipped 1: tea
            shipped 2: cups
            shipped 3: a kettle
            intake: closed
            shipper: done

            """,
            Run("05-worker.sb", OneThread)
        );

    [Fact]
    public void TheShopIsPutTogetherByItsContainer() =>
        Assert.Equal(
            """
            2 ordered
            1 ordered
              9 Examples.Shop.Transaction: begun
              9 Examples.Shop.MemoryRepository<Examples.Shop.Order>: 1 kept
              9 Examples.Shop.MemoryRepository<Examples.Shop.Receipt>: 1 kept
              9 Examples.Shop.Checkout: tea for 85 kr, thank you
              9 Examples.Shop.MemoryRepository<Examples.Shop.Order>: 2 kept
              9 Examples.Shop.MemoryRepository<Examples.Shop.Receipt>: 2 kept
              9 Examples.Shop.Checkout: cups for 85 kr, thank you
              9 Examples.Shop.Transaction: committed, 4 changed
              9 Examples.Shop.Transaction: begun
              9 Examples.Shop.MemoryRepository<Examples.Shop.Order>: 1 kept
              9 Examples.Shop.MemoryRepository<Examples.Shop.Receipt>: 1 kept
              9 Examples.Shop.Checkout: a kettle for 85 kr, thank you
              9 Examples.Shop.Transaction: committed, 2 changed
              23 Examples.Shop.Transaction: begun
              23 Examples.Shop.MemoryRepository<Examples.Shop.Order>: 1 kept
              23 Examples.Shop.MemoryRepository<Examples.Shop.Receipt>: 1 kept
              23 Examples.Shop.Checkout: a teapot for 175 kr, thank you
              23 Examples.Shop.Transaction: committed, 2 changed
            nothing provides any Examples.Shop.Clock: Examples.Shop.Transaction -> Examples.Shop.Logger<Examples.Shop.Transaction> -> any Examples.Shop.Clock

            """,
            Run("06-shop.sb")
        );

    [Fact]
    public void TheEchoServerTalksToItself() =>
        Assert.Equal("hello\n100 of 100 heard themselves\nechoed 995 bytes\n", Run("05-echo-server.sb"));

    /// <summary>Run with no pool, for the service: which of its workers serves which request depends on how they interleave.</summary>
    static string Run(string example, IReadOnlyDictionary<string, string>? environment = null) =>
        AssertAgree(File.ReadAllText(Path.Combine(Root, "examples", example)), stepLimit: 50_000_000, environment: environment).Stdout;

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
