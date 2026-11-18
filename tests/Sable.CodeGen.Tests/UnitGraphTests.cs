using Sable.Driver.Build;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Which units a unit depends on (docs/05 §5.4): its key covers their
///     interfaces, so a change to one compiles again only the units that
///     depend on it.
/// </summary>
public sealed class UnitGraphTests {
    static UnitFacts Unit(string[] modules, string[]? imports = null, string[]? references = null) =>
        new(modules.ToHashSet(), imports ?? [], references ?? []);

    static IReadOnlyList<IReadOnlyList<int>> Graph(params UnitFacts[] units) => UnitGraph.Dependencies(units);

    [Fact]
    public void AUnitDependsOnWhatItImportsAndWhatThatDependsOn() {
        var graph = Graph(
            Unit(["App"], ["Geometry"]),
            Unit(["Geometry"], ["Shapes", "Sable.IO"]),
            Unit(["Shapes"]),
            Unit(["Unused"]));
        Assert.Equal([1, 2], graph[0].Take(2));
        Assert.Equal([2], graph[1]);
        Assert.Empty(graph[2]);
        Assert.Empty(graph[3]);
    }

    [Fact]
    public void TheEntryDependsOnEveryUnit() =>
        // It sets up every global, its dependencies' or not.
        Assert.Equal([1, 2, 3], Graph(Unit(["App"]), Unit(["A"]), Unit(["B"], ["A"]), Unit(["C"]))[0]);

    [Fact]
    public void ANameWrittenInFullNeedsNoImport() {
        var graph = Graph(Unit(["App"]), Unit(["Lib"], references: ["Deep.Inner.Thing.method", "x.y"]), Unit(["Deep.Inner"]), Unit(["Deep"]));
        Assert.Equal([2, 3], graph[1]);
    }

    [Fact]
    public void AnImportReachesTheModulesBelowIt() {
        // 'import Sable as S' then 'S.Math.f()'.
        var graph = Graph(Unit(["App"]), Unit(["Lib"], ["Outer"]), Unit(["Outer.Inner"]), Unit(["Other"]));
        Assert.Equal([2], graph[1]);
    }

    [Fact]
    public void UnitsOfOneModuleDependOnEachOther() {
        // What one declares in the module the other sees as its own: a
        // subclass of a class that is not open, an overload.
        var graph = Graph(Unit(["App"]), Unit(["Shared"]), Unit(["Shared", "Other"]), Unit(["Above"], ["Other"]));
        Assert.Equal([2], graph[1]);
        Assert.Equal([1], graph[2]);
        Assert.Equal([1, 2], graph[3]);
    }
}
