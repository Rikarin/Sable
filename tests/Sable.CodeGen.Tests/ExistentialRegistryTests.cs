using System.Text.RegularExpressions;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Witness tables in conformance records (docs/06 §6.2, #297 stage 2):
///     <c>e as? any Q</c> and <c>e is any Q</c> on an existential ask the
///     conformance registry, from the metadata of what the box holds, for Q's
///     witness table — a struct's, a union's, a generic instance's, an object's
///     own class's or a superclass's. Native and interpreted, leak-checked.
/// </summary>
public sealed class ExistentialRegistryTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    const string Declarations = """
        protocol Named {
            fn name() -> String
        }

        protocol Sized {
            var size: Int { get }
        }

        protocol Loud {}

        struct Point: Named, Sized {
            var x: Int
            fn name() -> String => "point \(x)"
            var size: Int => x * 2
        }

        union Token: Named {
            Word(String)
            Number(Int)

            fn name() -> String {
                return match self {
                    case .Word(let w) => "word " + w
                    case .Number(let n) => "number \(n)"
                }
            }
        }

        struct Pair<T>: Named, Sized {
            var first: T
            var count: Int
            fn name() -> String => "pair of \(first)"
            var size: Int => count
        }

        open class Animal: Named {
            var called: String
            init(_ called: String) { self.called = called }
            open fn name() -> String => "animal " + called
        }

        class Dog: Animal, Sized, Loud {
            override fn name() -> String => "dog " + called
            var size: Int => 30
        }

        class Puppy: Dog {
            override fn name() -> String => "puppy " + called
        }

        fn describe(_ value: any Named) -> String {
            var text = value.name()
            if let sized = value as? any Sized {
                text += ", size \(sized.size)"
            }
            if let both = value as? any Named & Sized {
                text += ", both \(both.name())"
            }
            if value is any Loud {
                text += ", loud"
            }
            return text
        }
        """;

    [Fact]
    public void AnExistentialIsCastToAnotherProtocolThroughTheRegistry() =>
        Prints(
            """
            point 3, size 6, both point 3
            word hi
            pair of a, size 2, both pair of a
            animal cat
            dog rex, size 30, both dog rex, loud
            puppy bo, size 30, both puppy bo, loud

            """,
            Declarations,
            """
                print(describe(Point { x: 3 }))
                print(describe(Token.Word("h" + "i")))
                print(describe(Pair { first: "a" + "", count: 2 }))
                print(describe(Animal("cat")))
                print(describe(Dog("rex")))
                let animal: Animal = Puppy("bo")
                print(describe(animal))
            """
        );

    /// <summary>
    ///     An object cast to a protocol (not from an existential) is boxed as
    ///     its static class and asked the same: the registry walks its own
    ///     class's records, then its superclasses'.
    /// </summary>
    [Fact]
    public void AnObjectIsCastToAProtocolItsSubclassConformsTo() =>
        Prints(
            "30\nnone\ntrue false\n",
            Declarations,
            """
                let animals: Animal[>..] = [Puppy("p"), Animal("a")]
                for a in animals {
                    if let s = a as? any Sized { print(s.size) } else { print("none") }
                }
                print("\(animals[0] is Loud) \(animals[1] is Loud)")
            """
        );

    /// <summary>
    ///     The cast finds the table and builds the existential from it: a
    ///     call of <c>sable_witness_table</c>, and the conformance record
    ///     pointing at the table, which holds its witnesses inline, in the
    ///     order the protocol declares its requirements (#299).
    /// </summary>
    [Fact]
    public void AConformanceRecordPointsAtItsWitnessTable() {
        var source = Program(Declarations, "    print(describe(Point { x: 1 }))");
        // Read with its symbols as their SIL names (SymbolNames).
        var ir = SymbolNames.DemangleIr(Pipeline.Compile(new SourceText(source, "test.sb")).LlvmIr!);
        Assert.Contains("@sable_witness_table(ptr", ir, StringComparison.Ordinal);
        var records = ir.Split('\n').Single(l => l.StartsWith("@\"sable.conformances.Test.Point\" = ", StringComparison.Ordinal));
        // Its pointers to data are distances from their fields but on Windows (#300).
        string Ref(string target) => OperatingSystem.IsWindows() ? $"ptr {target}" : $"i64 sub (i64 ptrtoint (ptr {target} to i64)";
        var none = OperatingSystem.IsWindows() ? "ptr null" : "i64 0";
        Assert.Matches($"{Regex.Escape(Ref("@\"sable.protocol.Test.Sized\""))}.*{Regex.Escape(Ref("@\"sable.witnesses.Test.Point.Test.Sized\""))}", records);
        var table = ir.Split('\n').Single(l => l.StartsWith("@\"sable.witnesses.Test.Point.Test.Sized\" = ", StringComparison.Ordinal));
        Assert.Contains($"{{ {Ref("@\"sable.protocol.Test.Sized\"")}", table, StringComparison.Ordinal);
        Assert.Contains($"i64 0, {none}, i64 0, {none}, i64 0, {none}, i64 1, [1 x ptr] [ptr @\"sable.$generic$witness$Test.Point$Test.Sized$0$", table, StringComparison.Ordinal);
        Assert.DoesNotContain("$cast$to$any$Test.Sized\"(ptr %0) {\nentry:\n  %t0 = call zeroext i1 @sable_class_is", ir, StringComparison.Ordinal);
    }
}
