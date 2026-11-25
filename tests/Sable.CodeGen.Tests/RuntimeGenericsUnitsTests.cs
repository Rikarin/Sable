using Sable.Driver.Build;
using Sable.SIL;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Generics known only as a program runs (#297), in a program compiled in
///     units: each unit that opens a type makes what it opens of the library's
///     generic types, and the linker keeps one.
/// </summary>
[Native]
public sealed class RuntimeGenericsUnitsTests {
    const string Opens = """
        module Opens

        public protocol Named { fn name() -> String }
        public struct Crate<T: Hashable>: Named {
            public var item: T
            public init(item: T) { self.item = item }
            public fn name() -> String => "crate of \(item)"
        }

        public fn crateOf<T: Hashable>(_ type: T.Type) -> Type => Crate<T>.self
        public fn hashable<T: Hashable>(_ type: T.Type) -> String => "\(type)"
        public fn described(_ x: any Any) -> Bool => x is any Formattable
        """;

    const string Main = """
        module Main

        import Opens

        fn equatable<T: Equatable>(_ type: T.Type) -> String => "\(type)"
        fn ordered<T: Comparable>(_ type: T.Type) -> String => "\(type)"

        @main
        fn main() {
            let types: Type[>..] = [Int.self, Range<Int>.self]
            print("\(hashable(types[0])) \(equatable(types[1])) \(ordered(types[0])) \(crateOf(types[0]))")
            print("\(described(4)) \((4 as any Any) is any Hashable)")
        }
        """;

    /// <summary>With the library compiled in the same build, and then from the cache: neither falls back to a whole program.</summary>
    [Theory]
    [InlineData(GenericsMode.Specialised, false)]
    [InlineData(GenericsMode.Specialised, true)]
    [InlineData(GenericsMode.Unspecialised, false)]
    [InlineData(GenericsMode.Unspecialised, true)]
    public void UnitsThatOpenTypesLink(GenericsMode mode, bool release) {
        if (!Programs.CanBuild) {
            return;
        }

        using var generics = SilGen.UseGenerics(mode);
        var directory = Directory.CreateTempSubdirectory("sable-opens-").FullName;
        try {
            UnitPlan Unit(string name, string source) {
                var path = Path.Combine(directory, name + ".sb");
                File.WriteAllText(path, source);
                return new(name, [new SourceFile(path, name + ".sb", [])]);
            }

            UnitPlan[] units = [Unit("opens", Opens), Unit("main", Main)];
            var settings = release
                ? new BuildSettings("release", true, null, false) { Native = new NativeOptions { OptLevel = "3" } }
                : new BuildSettings("dev", false, directory, true);
            var cache = new BuildCache(Path.Combine(directory, "shared"));
            var executable = Platform.Executable(Path.Combine(directory, "program"));
            for (var round = 0; round < 2; round++) {
                var outcome = IncrementalBuild.Build(units, settings, executable, Path.Combine(directory, "cache"), cache: cache);
                Assert.True(outcome.Succeeded, string.Join("\n", outcome.Errors));
                Assert.True(outcome.FellBack is null, outcome.FellBack);
                Assert.Equal(("Int Sable.Core.Range<Int> Int Opens.Crate<Int>\ntrue true\n", 0), PackageFixture.Execute(executable));
                foreach (var unit in units) {
                    File.AppendAllText(unit.Files[0].Path, "\n// changed\n");
                }
            }
        } finally {
            Directory.Delete(directory, true);
        }
    }
}
