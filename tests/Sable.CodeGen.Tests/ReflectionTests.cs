using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Reflection (docs/06 §6.3): <c>Mirror(of:)</c> on any value and
///     <c>TypeInfo.of</c> on a <c>@reflectable</c> type, native and
///     interpreted alike, leak-checked.
/// </summary>
public sealed class ReflectionTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void AMirrorShowsAStructsTypeFieldsAndValues() =>
        Prints(
            "Point struct 2\nPoint(x: 1, y: -2)\nx: Int = 1\ny: Int = -2\nnone\n-2\nmissing\n",
            "struct Point { var x: Int; var y: Int }",
            """
                let m = Mirror(of: Point(x: 1, y: -2))
                print("\(m.typeName) \(m.kind) \(m.count)")
                print(m.description)
                for f in m.fields {
                    print("\(f.name): \(f.typeName) = \(f.value)")
                }
                print(m.caseName ?? "none")
                if let y = m.field(named: "y") {
                    print(y.value)
                }
                if m.field(named: "z") == none {
                    print("missing")
                }
            """
        );

    [Fact]
    public void AMirrorNamesTheCaseOfAUnionAnEnumAndAnOptional() =>
        Prints(
            "Shape.Circle(radius: 2.5)\nCircle radius F64 2.5\nShape.Rect(3, 4)\n0=3 1=4\nShape.Empty 0\nColor.Green Green enum\nsome 7\nnone 0 none\n",
            """
            union Shape {
                Circle(radius: F64)
                Rect(Int, Int)
                Empty
            }
            enum Color {
                Red
                Green
            }
            """,
            """
                let c = Mirror(of: Shape.Circle(radius: 2.5))
                print(c.description)
                print("\(c.caseName ?? "") \(c.fields[0].name) \(c.fields[0].typeName) \(c.fields[0].value)")
                let r = Mirror(of: Shape.Rect(3, 4))
                print(r.description)
                print("\(r.fields[0].name)=\(r.fields[0].value) \(r.fields[1].name)=\(r.fields[1].value)")
                let e = Mirror(of: Shape.Empty)
                print("\(e.description) \(e.count)")
                let g = Mirror(of: Color.Green)
                print("\(g.description) \(g.caseName ?? "") \(g.kind)")
                let present: Int? = 7
                let s = Mirror(of: present)
                print("\(s.caseName ?? "") \(s.fields[0].value)")
                let nothing: Int? = none
                let n = Mirror(of: nothing)
                print("\(n.caseName ?? "") \(n.count) \(n.description)")
            """
        );

    [Fact]
    public void ScalarsCollectionsAndTuplesAreWrittenOut() =>
        Prints(
            "42\n1.5\ntrue\n\"text\"\n'c'\n[1, 2, 3]\n[[\"a\"], []]\n[\"one\": 1]\n[:]\n(1, \"x\")\n(x: 1, y: 2)\narray 3 Int\n0 1 2\nprimitive 0\n",
            "",
            """
                print(Reflect.describe(42))
                print(Reflect.describe(1.5))
                print(Reflect.describe(true))
                print(Reflect.describe("text"))
                print(Reflect.describe('c'))
                print(Reflect.describe([1, 2, 3]))
                let nested: String[>..][>..] = [["a"], []]
                print(Reflect.describe(nested))
                print(Reflect.describe(["one": 1]))
                let empty: [String: Int] = [:]
                print(Reflect.describe(empty))
                print(Reflect.describe((1, "x")))
                print(Reflect.describe((x: 1, y: 2)))
                let a = Mirror(of: [10, 20, 30])
                print("\(a.kind) \(a.count) \(a.fields[0].typeName)")
                print("\(a.fields[0].name) \(a.fields[1].name) \(a.fields[2].name)")
                let i = Mirror(of: 5)
                print("\(i.kind) \(i.count)")
            """
        );

    [Fact]
    public void ObjectsAreFollowedAndACycleStops() =>
        Prints(
            "Node(value: 1, next: Node(value: 2, next: none))\n"
            + "Ring(id: 1, next: Ring(id: 2, next: Ring(id: 1, next: Ring(id: 2, next: Ring(id: 1, next: Ring(id: 2, next: Ring(id: 1, next: Ring(id: 2, next: ...))))))))\n"
            + "class Node? Node(value: 2, next: none)\n",
            """
            class Node {
                var value: Int
                var next: Node?
                init(value: Int, next: Node?) {
                    self.value = value
                    self.next = next
                }
            }
            class Ring {
                var id: Int
                var next: Ring?
                init(id: Int) {
                    self.id = id
                    self.next = none
                }
            }
            """,
            """
                let list = Node(value: 1, next: Node(value: 2, next: none))
                print(Reflect.describe(list))
                let a = Ring(id: 1)
                let b = Ring(id: 2)
                a.next = b
                b.next = a
                print(Reflect.describe(a))
                b.next = none
                let m = Mirror(of: list)
                print("\(m.kind) \(m.fields[1].typeName) \(m.fields[1].value)")
            """
        );

    [Fact]
    public void ASubclassShowsItsSuperclassFieldsFirstAndAWeakFieldIsReadWithoutKeepingIt() =>
        Prints(
            "Dog(name: \"rex\", legs: 4, owner: Person(name: \"ann\"))\nname legs owner\nDog(name: \"rex\", legs: 4, owner: none)\n",
            """
            class Person {
                var name: String
                init(name: String) { self.name = name }
            }
            open class Animal {
                var name: String
                init(name: String) { self.name = name }
            }
            class Dog: Animal {
                var legs: Int
                weak var owner: Person?
                init(name: String, legs: Int) {
                    super.init(name: name)
                    self.legs = legs
                    self.owner = none
                }
            }
            """,
            """
                let d = Dog(name: "rex", legs: 4)
                var ann: Person? = Person(name: "ann")
                d.owner = ann
                print(Reflect.describe(d))
                let m = Mirror(of: d)
                print("\(m.fields[0].name) \(m.fields[1].name) \(m.fields[2].name)")
                ann = none
                print(Reflect.describe(d))
            """
        );

    [Fact]
    public void GenericCodeMirrorsWhateverItIsGiven() =>
        Prints(
            "Box<Int>: Box<Int>(value: 3, label: \"three\")\nBox<String>: Box<String>(value: \"s\", label: \"ess\")\nInt[>..]: [1]\n",
            """
            struct Box<T> {
                var value: T
                var label: String
            }
            fn dump<T>(_ value: T) {
                let m = Mirror(of: value)
                print("\(m.typeName): \(m.description)")
            }
            """,
            """
                dump(Box(value: 3, label: "three"))
                dump(Box(value: "s", label: "ess"))
                let one: Int[>..] = [1]
                dump(one)
            """
        );

    [Fact]
    public void TypeInfoDescribesAReflectableDeclaration() =>
        Prints(
            "Account struct\nowner String false true\nbalance Int true false\n"
            + "public fn summary() -> String\nmutating fn deposit(_ amount: Int)\nstatic fn empty(for owner: String) -> Account\n"
            + "operator ==(lhs: Account, rhs: Account) -> Bool\ninit(owner: String, balance: Int)\n"
            + "Equatable Reflectable\nreflectable\ntrue false true false\nInt amount _ deposit\nmutating true\n",
            """
            @reflectable
            struct Account: Equatable {
                public let owner: String
                var balance: Int
                public fn summary() -> String { return owner }
                mutating fn deposit(_ amount: Int) { balance = balance + amount }
                static fn empty(for owner: String) -> Account { return Account(owner: owner, balance: 0) }
                operator == (lhs: Account, rhs: Account) -> Bool { return lhs.owner == rhs.owner }
            }
            """,
            """
                let t = TypeInfo.of(Account.self)
                print("\(t.name) \(t.kind)")
                for f in t.fields {
                    print("\(f.name) \(f.typeName) \(f.isMutable) \(f.isPublic)")
                }
                for m in t.methods {
                    print((m.isPublic ? "public " : "") + m.signature)
                }
                print("\(t.conformances[0]) \(t.conformances[1])")
                print(t.attributeNames[0])
                print("\(t.conforms(to: "Equatable")) \(t.conforms(to: "Hashable")) \(t.hasAttribute("reflectable")) \(t.hasAttribute("frozen"))")
                if let deposit = t.method(named: "deposit") {
                    print("\(deposit.parameterTypeNames[0]) \(deposit.parameterNames[0]) \(deposit.returnTypeName == "Void" ? "_" : deposit.returnTypeName) \(deposit.name)")
                    print("\(deposit.isMutating ? "mutating" : "plain") \(t.field(named: "balance") != none)")
                }
            """
        );

    [Fact]
    public void TypeInfoListsAUnionsCasesAndWorksThroughAConstraint() =>
        Prints(
            "Token union 3\nNumber value Int\nWord 0 String\nEnd 0\nLevel enum Low,High\nPair<Int> 2 first:Int\n",
            """
            @reflectable
            union Token {
                Number(value: Int)
                Word(String)
                End
            }
            @reflectable
            enum Level {
                Low
                High
            }
            @reflectable
            struct Pair<T> {
                var first: T
                var second: T
            }
            fn describeType<T: Reflectable>(_ value: T) -> String {
                let t = TypeInfo.of(value)
                return "\(t.name) \(t.fields.count) \(t.fields[0].name):\(t.fields[0].typeName)"
            }
            """,
            """
                let t = TypeInfo.of(Token.self)
                print("\(t.name) \(t.kind) \(t.cases.count)")
                print("\(t.cases[0].name) \(t.cases[0].fieldNames[0]) \(t.cases[0].fieldTypeNames[0])")
                print("\(t.cases[1].name) \(t.cases[1].fieldNames[0]) \(t.cases[1].fieldTypeNames[0])")
                print("\(t.cases[2].name) \(t.cases[2].fieldNames.count)")
                let l = TypeInfo.of(Level.self)
                print("\(l.name) \(l.kind) \(l.cases[0].name),\(l.cases[1].name)")
                print(describeType(Pair(first: 1, second: 2)))
            """
        );

    [Fact]
    public void WhatCannotBeTakenApartIsNamed() =>
        Prints(
            "Holder(f: <(Int) -> Int>, n: <any Named>, c: 'z', u: 7, g: 1.5)\nn any Named <any Named>\n"
            + "Pet(owner: Owner(label: \"o\"), tags: [\"a\": 1])\nfunction existential\n() 0\n",
            """
            protocol Named { fn name() -> String }
            struct Dog: Named { fn name() -> String { return "rex" } }
            class Owner {
                var label: String
                init(label: String) { self.label = label }
            }
            class Pet {
                unowned var owner: Owner
                var tags: [String: Int]
                init(owner: Owner) {
                    self.owner = owner
                    self.tags = ["a": 1]
                }
            }
            struct Holder {
                var f: (Int) -> Int
                var n: any Named
                var c: Char
                var u: U8
                var g: F32
            }
            """,
            """
                let h = Holder(f: { x in x + 1 }, n: Dog(), c: 'z', u: 7, g: 1.5)
                print(Reflect.describe(h))
                let n = Mirror(of: h).fields[1]
                print("\(n.name) \(n.typeName) \(n.value)")
                let o = Owner(label: "o")
                print(Reflect.describe(Pet(owner: o)))
                print("\(Reflect.kind(h.f)) \(Reflect.kind(h.n))")
                print("\(Reflect.describe(())) \(Reflect.fieldNames(()).count)")
            """
        );

    /// <summary>
    ///     docs/04 §4.4: reflection costs a program nothing unless it
    ///     reflects, and then only for the types it reflects on.
    /// </summary>
    [Fact]
    public void OnlyTheTypesReflectedOnAreEmitted() {
        var plain = Pipeline.Compile(new SourceText(Program("struct Point { var x: Int }", "print(Point(x: 1).x)"), "test.sb"));
        Assert.True(plain.Succeeded, string.Join("\n", plain.Errors));
        Assert.DoesNotContain("reflect", plain.LlvmIr!);
        Assert.DoesNotContain("Mirror", plain.LlvmIr!);

        var reflecting = Pipeline.Compile(new SourceText(
            Program("struct Point { var x: Int }\nstruct Other { var y: Int }", "print(Reflect.describe(Point(x: 1)))\nprint(Other(y: 2).y)"),
            "test.sb"));
        Assert.True(reflecting.Succeeded, string.Join("\n", reflecting.Errors));
        Assert.Contains("$reflect.describe$Point", reflecting.LlvmIr!);
        Assert.DoesNotContain("$reflect.describe$Other", reflecting.LlvmIr!);
        Assert.DoesNotContain("TypeInfo", reflecting.LlvmIr!);
    }

    [Fact]
    public void InterpolationWritesAnyValue() =>
        // A string as it is; Debug as the type says; anything else as
        // Reflect.describe writes it; a generic hole by its instance's type.
        Prints(
            "Point(x: 1, y: 2) IoError.Denied Tagged(name: \"t\") custom!\n3 none [1, 2] (1, \"a\") Node(value: 4) ()\n<custom!> <3> <text>\n",
            """
            union IoError { NotFound(path: String), Denied }

            struct Point {
                var x: Int
                var y: Int
            }

            @derive(Debug)
            struct Tagged {
                var name: String
            }

            struct Custom: Debug {
                var n: Int
                fn debugDescription() -> String { return "custom!" }
            }

            class Node {
                var value: Int
                init(_ value: Int) {
                    self.value = value
                }
            }

            fn bracketed<T>(_ t: T) -> String {
                return "<\(t)>"
            }
            """,
            """
                let p = Point { x: 1, y: 2 }
                print("\(p) \(IoError.Denied) \(Tagged { name: "t" }) \(Custom { n: 1 })")
                let three: Int? = 3
                let nothing: Int? = none
                print("\(three) \(nothing) \([1, 2]) \((1, "a")) \(Node(4)) \(())")
                print(bracketed(Custom { n: 2 }) + " " + bracketed(3) + " " + bracketed("text"))
            """
        );

    [Fact]
    public void ACaughtErrorIsWrittenAsTheErrorItIs() =>
        Prints(
            "caught IoError.NotFound(path: \"a.txt\")\ncaught IoError.Denied\ncaught Point(x: 1, y: 2)\nok 3\n",
            """
            union IoError { NotFound(path: String), Denied }

            struct Point {
                var x: Int
                var y: Int
            }

            fn fail(_ k: Int) throws -> Int {
                if k == 0 { throw IoError.NotFound(path: "a.txt") }
                if k == 1 { throw IoError.Denied }
                if k == 2 { throw Point { x: 1, y: 2 } }
                return k
            }
            """,
            """
                for k in [0, 1, 2, 3] {
                    do {
                        let v = try fail(k)
                        print("ok \(v)")
                    } catch {
                        print("caught \(error)")
                    }
                }
            """
        );

    /// <summary>
    ///     An attribute of the program's own is a struct conforming to
    ///     Attribute, its arguments an initialiser's; a @reflectable type's
    ///     are values TypeInfo gives back, by their type, in the order written.
    /// </summary>
    [Fact]
    public void TheProgramsOwnAttributesAreValuesReflectionGives() =>
        Prints(
            "2\nGET /users\nPOST /users\n-3 2 b\n0\n4\n1\n",
            """
            struct Route: Attribute {
                var path: String
                var method: String = "GET"
            }

            struct Weight: Attribute {
                var value: Int
                var tags: String[>..]
            }

            struct Unused: Attribute { }

            @reflectable
            @Route("/users")
            @Weight(value: -3, tags: ["a", "b"])
            @Route("/users", method: "POST")
            struct Users {
                var count: Int
            }

            @Route("/plain")
            fn plain() -> Int { return 1 }
            """,
            """
                let info = TypeInfo.of(Users.self)
                let routes = info.attributes(of: Route.self)
                print(routes.count)
                for r in routes {
                    print("\(r.method) \(r.path)")
                }
                let w = info.attributes(of: Weight.self)[0]
                print("\(w.value) \(w.tags.count) \(w.tags[1])")
                print(info.attributes(of: Unused.self).count)
                print(info.attributeNames.count)
                print(plain())
            """
        );
}
