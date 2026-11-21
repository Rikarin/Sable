using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Type metadata (docs/06 §6.2, #297): a type's kind, layout, name,
///     fields, type arguments and value witnesses, for every kind of type —
///     natively from the records the code generator writes, whose layout
///     numbers are LLVM's own, and in the interpreter from its model, which
///     must agree. The value witnesses copy, move, assign and destroy, leak-checked.
/// </summary>
public sealed class TypeKindTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    const string Show = """
        fn show(_ m: Type) {
            print("\(m.name): \(m.kind) \(m.size)/\(m.alignment)/\(m.stride)\(m.isTrivial ? " trivial" : "")\(m.isNoncopyable ? " noncopyable" : "")")
        }
        """;

    [Fact]
    public void ScalarsAndStrings() =>
        Prints(
            """
            Int: Scalar 8/8/8 trivial
            I8: Scalar 1/1/1 trivial
            U16: Scalar 2/2/2 trivial
            I128: Scalar 16/16/16 trivial
            F32: Scalar 4/4/4 trivial
            F64: Scalar 8/8/8 trivial
            Bool: Scalar 1/1/1 trivial
            Char: Scalar 4/4/4 trivial
            String: String 8/8/8
            Type: Metatype 8/8/8 trivial

            """,
            Show,
            """
                show(Int.self)
                show(I8.self)
                show(U16.self)
                show(I128.self)
                show(F32.self)
                show(F64.self)
                show(Bool.self)
                show(Char.self)
                show(String.self)
                show(Type.self)
            """
        );

    /// <summary>Structs as laid out — '@packed' and '@align' among them — classes as references, and their fields' offsets.</summary>
    [Fact]
    public void StructsAndClassesAndTheirFields() =>
        Prints(
            """
            Test.Point: Struct 24/8/24
              x @0: Int
              flag @8: Bool
              name @16: String
            Test.Header: Struct 7/1/7 trivial
              tag @0: U8
              length @1: U32
              flags @5: U16
            Test.Vec: Struct 16/16/16 trivial
              x @0: F32
              y @4: F32
            Test.Animal: Class 8/8/8
              name @16: String
              legs @24: Int
            Test.Dog: Class 8/8/8
              name @16: String
              legs @24: Int
              good @32: Bool
              friend @40: Test.Dog?
            Test.Empty: Struct 0/1/0 trivial

            """,
            Show + """

            struct Point {
                var x: Int
                var flag: Bool
                var name: String
            }

            @packed
            struct Header {
                var tag: U8
                var length: U32
                var flags: U16
            }

            @align(16)
            struct Vec {
                var x: F32
                var y: F32
            }

            open class Animal {
                var name: String = "a"
                var legs: Int = 4
            }

            class Dog: Animal {
                var good: Bool = true
                var friend: Dog? = none
            }

            struct Empty {}

            fn fields(_ m: Type) {
                show(m)
                var i = 0
                while i < m.fieldCount {
                    print("  \(m.fieldName(at: i)) @\(m.fieldOffset(at: i)): \(m.fieldType(at: i).name)")
                    i += 1
                }
            }
            """,
            """
                fields(Point.self)
                fields(Header.self)
                fields(Vec.self)
                fields(Animal.self)
                fields(Dog.self)
                fields(Empty.self)
            """
        );

    /// <summary>Unions, enums and optionals: a tag and a payload area, a niche, a nullable reference; their cases as fields.</summary>
    [Fact]
    public void UnionsEnumsAndOptionals() =>
        Prints(
            """
            Test.Shape: Union 16/8/16
              Circle @8: F64
              Label @8: String
              Empty @8
            Test.Colour: Enum 1/1/1 trivial
              Red @1
              Green @1
            Int?: Optional 16/8/16 trivial
            Bool?: Optional 1/1/1 trivial
            Char?: Optional 4/4/4 trivial
            String?: Optional 8/8/8
            Test.Node?: Optional 8/8/8
            Int?: Int

            """,
            Show + """

            union Shape {
                Circle(F64)
                Label(String)
                Empty
            }

            enum Colour {
                Red
                Green
            }

            class Node {
                var next: Node? = none
            }

            fn cases(_ m: Type) {
                show(m)
                var i = 0
                while i < m.fieldCount {
                    let payload = m.fieldName(at: i) == "Empty" || m.kind == .Enum ? "" : ": \(m.fieldType(at: i).name)"
                    print("  \(m.fieldName(at: i)) @\(m.fieldOffset(at: i))\(payload)")
                    i += 1
                }
            }
            """,
            """
                cases(Shape.self)
                cases(Colour.self)
                show(Int?.self)
                show(Bool?.self)
                show(Char?.self)
                show(String?.self)
                show(Node?.self)
                let optional: Type = Int?.self
                print("\(optional.name): \(optional.argument(at: 0).name)")
            """
        );

    /// <summary>Tuples, functions, arrays, slices, existentials and generic instances, with their type arguments.</summary>
    [Fact]
    public void StructuralAndGenericTypes() =>
        Prints(
            """
            (Int, Bool, String): Tuple 24/8/24
              0 @0: Int
              1 @8: Bool
              2 @16: String
            (Int) -> Bool: Function 16/8/16
              arguments: Int Bool
            [String]: Array 8/8/8
              arguments: String
            [Int:]: Slice 24/8/24
              arguments: Int
            any Test.Named: Existential 24/8/24
            Test.Pair<Int, String>: Struct 16/8/16
              arguments: Int String
            Test.Pair<Int, String>
            true false

            """,
            Show + """

            protocol Named {
                var name: String { get }
            }

            struct Pair<A, B> {
                var first: A
                var second: B
            }

            fn arguments(_ m: Type) {
                show(m)
                var names = ""
                var i = 0
                while i < m.argumentCount {
                    names += " " + m.argument(at: i).name
                    i += 1
                }
                print("  arguments:\(names)")
            }

            fn same<T>(_ value: T) -> Type => T.self
            """,
            """
                let tuple: Type = (Int, Bool, String).self
                show(tuple)
                var i = 0
                while i < tuple.fieldCount {
                    print("  \(tuple.fieldName(at: i)) @\(tuple.fieldOffset(at: i)): \(tuple.fieldType(at: i).name)")
                    i += 1
                }
                arguments(((Int) -> Bool).self)
                arguments(String[>..].self)
                arguments(Int[:].self)
                show((any Named).self)
                arguments(Pair<Int, String>.self)
                print(same(Pair { first: 1, second: "s" }).name)
                print("\(same(1) == Int.self) \(same(1) == I64.self)")
            """
        );

    /// <summary>
    ///     The value witnesses, as unspecialised code will call them: a copy,
    ///     a move, an assignment and a destruction of a value of each kind
    ///     that holds references — each count balanced, which the leak check
    ///     and the interpreter's counting both hold to.
    /// </summary>
    [Fact]
    public void ValueWitnessesCopyMoveAssignAndDestroy() =>
        Prints(
            """
            object 7 7 7
            struct ab ab cd
            union Label(x) Label(x)
            optional a
            tuple 3 c
            array 3 3
            closure 42
            existential n
            string hello
            noncopyable true

            """,
            """
            class Box {
                var value: Int
                init(_ value: Int) { self.value = value }
            }

            struct Named {
                var name: String
                var box: Box
            }

            union Tag {
                Label(String)
                Count(Int)
            }

            protocol P {
                fn get() -> String
            }

            struct Q: P {
                var s: String
                fn get() -> String => s
            }

            @noncopy
            struct Unique {
                var id: Int
            }

            fn describe(_ tag: Tag) -> String {
                return match tag {
                    case .Label(let s) => "Label(\(s))"
                    case .Count(let n) => "Count(\(n))"
                }
            }

            fn exercise<T>(_ value: T, _ other: T) -> T {
                let m: Type = T.self
                let copied = m.copy(value)
                let moved = m.move(copied)
                m.destroyCopy(of: value)
                var target = other
                m.assign(moved, to: &target)
                return target
            }
            """,
            """
                let b = Box(7)
                let b2 = exercise(b, Box(8))
                print("object \(b.value) \(b2.value) \(Box.self.copy(b).value)")
                let n = Named { name: "a" + "b", box: Box(1) }
                let n2 = exercise(n, Named { name: "c" + "d", box: Box(2) })
                var n3 = n
                Named.self.assign(Named { name: "c" + "d", box: Box(3) }, to: &n3)
                print("struct \(n.name) \(n2.name) \(n3.name)")
                let t = Tag.Label("x" + "")
                print("union \(describe(exercise(t, .Count(1)))) \(describe(t))")
                let o: String? = "a" + ""
                print("optional \(exercise(o, none) ?? "none")")
                let pair = (3, "c" + "")
                let pair2 = exercise(pair, (4, "d" + ""))
                print("tuple \(pair2.0) \(pair2.1)")
                let xs = [1, 2, 3]
                print("array \(exercise(xs, [4]).count) \(xs.count)")
                let captured = Box(42)
                let f = { () -> Int in captured.value }
                print("closure \(exercise(f, { () -> Int in 0 })())")
                let e: any P = Q { s: "n" + "" }
                print("existential \(exercise(e, Q { s: "m" }).get())")
                print("string \(exercise("hel" + "lo", "x"))")
                print("noncopyable \(Unique.self.isNoncopyable)")
            """
        );

    /// <summary>Metadata given another type's value is refused: a witness would misread it.</summary>
    [Fact]
    public void AWitnessOfAnotherTypeTraps() {
        var run = AssertAgree(Program("", """
                let m: Type = String.self
                print(m.copy(1))
            """));
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("the metadata is not of the value's type", run.Stderr);
    }
}
