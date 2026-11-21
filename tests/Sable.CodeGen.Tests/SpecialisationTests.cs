using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The specialisation pass (#297 stage 5) in an optimised build: generic
///     code lowered once and specialised by the optimiser runs, natively and
///     in the interpreter, as the specialised lowering's does.
/// </summary>
[Native]
public sealed class SpecialisationTests {
    /// <summary>The program optimised — specialised by the pass — and interpreted: its output.</summary>
    static string InterpretOptimised(string source) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"), optimised: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Sil!.Functions, static f => f.IsUnspecialised && f.Name.StartsWith("Sable.Core.Dictionary.", StringComparison.Ordinal));
        var interpreter = new SilInterpreter(result.Sil!, 100_000_000);
        interpreter.Run(result.Sil!.EntryPoint!);
        Assert.Equal(0, interpreter.LiveObjects);
        return interpreter.Output.ToString();
    }

    static void PrintsTheSameEachWay(string expected, string source) {
        string specialised, unspecialised, interpreted;
        using (SilGen.UseGenerics(GenericsMode.Specialised)) {
            specialised = CanBuild ? BuildAndRun(source).Stdout : expected;
        }

        using (SilGen.UseGenerics(GenericsMode.Unspecialised)) {
            unspecialised = CanBuild ? BuildAndRun(source).Stdout : expected;
            interpreted = InterpretOptimised(source);
        }

        Assert.Equal(expected, specialised);
        Assert.Equal(expected, unspecialised);
        Assert.Equal(expected, interpreted);
    }

    /// <summary>
    ///     A dictionary's lookups in a loop, each a call the inliner copies a
    ///     slot of into the loop: the slot is the frame's, made once, so the
    ///     loop does not grow the stack.
    /// </summary>
    [Fact]
    public void ADictionaryOfStringsLookedUpInALoopRunsInAFixedFrame() {
        PrintsTheSameEachWay("20000 19999\n", Program("", """
            var map: [String: Int] = [:]
            var keys: String[>..] = []
            var i = 0
            while i < 20000 {
                keys.append("key-\(i)")
                map[keys[i]] = i
                i = i + 1
            }
            var hits = 0
            var last = 0
            var round = 0
            while round < 20 {
                for key in keys {
                    if let value = map[key] {
                        hits = hits + 1
                        last = value
                    }
                }
                round = round + 1
            }
            print("\(hits / 20) \(last)")
            """));
    }

    /// <summary>A chain of lazy sequences, its closures reabstracted for generic code, and a protocol's requirements called on a class and a struct.</summary>
    [Fact]
    public void ClosuresAndRequirementsRunAsSpecialisedCodeDoes() {
        PrintsTheSameEachWay("254\nsquare 16 circle 12\n", Program("""
            protocol Shape {
                fn area() -> Int
                fn name() -> String
            }

            struct Square: Shape {
                var side: Int
                fn area() -> Int => side * side
                fn name() -> String => "square"
            }

            final class Circle: Shape {
                var r: Int
                init(r: Int) { self.r = r }
                fn area() -> Int => 3 * r * r
                fn name() -> String => "circle"
            }

            fn describe<S: Shape>(_ shape: S) -> String {
                return shape.name() + " \(shape.area())"
            }
            """, """
            var values: Int[>..] = []
            for i in 0..<20 {
                values.append(i)
            }
            let modulus = 3
            print(values.lazy.filter { $0 % modulus != 0 }.map { $0 * 2 }.reduce(0) { $0 + $1 })
            print(describe(Square { side: 4 }) + " " + describe(Circle(r: 2)))
            """));
    }
}
