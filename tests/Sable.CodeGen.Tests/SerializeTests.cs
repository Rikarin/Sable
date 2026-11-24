using Sable.Driver;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>@derive(Serializable)</c> and <c>@serialize(…)</c> (docs/06 §6.5):
///     a derived round trip, native and interpreted, and every option refused
///     at the option.
/// </summary>
public sealed class SerializeTests {
    static string SingleError(string declarations) {
        var result = Pipeline.Compile(new SourceText($"module M\nimport Sable.Serialize\n\n{declarations}\n\n@main\nfn main() {{ }}\n", "test.sb"));
        Assert.True(result.Errors.Count == 1, string.Join("\n", result.Errors));
        return result.Errors[0];
    }

    [Fact]
    public void ADerivedRoundTripAgrees() =>
        Assert.Equal(
            """
            {"x":1,"why":2,"label":"p"}
            "amber"
            {"type":"Circle","radius":1.5}
            {"type":"Empty"}
            {"t":"Pair","c":[3,4]}
            true true true
            $.why: missing
            $: no case 'Blue'

            """,
            AssertAgree("""
                module P
                import Sable.Serialize

                @derive(Equatable, Serializable)
                struct Point {
                    var x: Int
                    @serialize(rename: "why")
                    var y: Int
                    @serialize(skip)
                    var cache: Int = 7
                    @serialize(defaulted)
                    var label: String = ""
                }

                @derive(Equatable, Serializable)
                enum Light {
                    Red
                    @serialize(rename: "amber")
                    Yellow
                }

                @derive(Equatable, Serializable)
                @serialize(tag: "type")
                union Shape {
                    Circle(radius: F64)
                    Empty
                }

                @derive(Equatable, Serializable)
                @serialize(tag: "t", content: "c")
                union Message {
                    Text(String)
                    Pair(Int, Int)
                }

                fn describe(_ error: SerializeError) -> String {
                    return error.description()
                }

                @main
                fn main() throws(SerializeError) {
                    let p = Point(x: 1, y: 2, cache: 9, label: "p")
                    print(try Json.encode(p))
                    print(try Json.encode(Light.Yellow))
                    print(try Json.encode(Shape.Circle(radius: 1.5)))
                    print(try Json.encode(Shape.Empty))
                    print(try Json.encode(Message.Pair(3, 4)))
                    let back = try Json.decode(Point.self, from: "{\"x\":1,\"why\":2}")
                    let shape = try Json.decode(Shape.self, from: try Json.encode(Shape.Circle(radius: 1.5)))
                    let message = try Json.decode(Message.self, from: try Json.encode(Message.Text("hi")))
                    print("\(back == Point(x: 1, y: 2, cache: 7, label: "")) \(shape == Shape.Circle(radius: 1.5)) \(message == Message.Text("hi"))")
                    do {
                        let _ = try Json.decode(Point.self, from: "{\"x\":1}")
                    } catch {
                        print(describe(error))
                    }
                    do {
                        let _ = try Json.decode(Light.self, from: "\"Blue\"")
                    } catch {
                        print(describe(error))
                    }
                }
                """).Stdout
        );

    /// <summary>A union that holds itself, and a generic struct with what it is of flattened, compiled each way.</summary>
    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void AnUntaggedUnionOfItselfAndAFlattenedParameterAgree(GenericsMode generics) {
        using var _ = SilGen.UseGenerics(generics);
        Assert.Equal(
            """
            {"at":1,"x":2,"deep":[[],"t",null]}
            {"at":1,"x":2,"deep":[[],"t",null]}
            $.deep: expected a case of Tree, found a Bool

            """,
            AssertAgree("""
                module P
                import Sable.Serialize

                @derive(Serializable)
                @serialize(untagged)
                union Tree {
                    Many(Tree[>..])
                    Text(String)
                    Nothing
                }

                @derive(Serializable)
                struct Inner {
                    var x: Int
                    var deep: Tree
                }

                @derive(Serializable)
                struct Logged<T> {
                    var at: Int
                    @serialize(flatten)
                    var what: T
                }

                fn describe(_ error: SerializeError) -> String {
                    return error.description()
                }

                @main
                fn main() throws(SerializeError) {
                    let value: Logged<Inner> = Logged(at: 1, what: Inner(x: 2, deep: .Many([.Many([]), .Text("t"), .Nothing])))
                    let text = try Json.encode(value)
                    print(text)
                    print(try Json.encode(try Json.decode(Logged<Inner>.self, from: text)))
                    do {
                        let _ = try Json.decode(Logged<Inner>.self, from: "{\"at\":1,\"x\":2,\"deep\":true}")
                    } catch {
                        print(describe(error))
                    }
                }
                """).Stdout
        );
    }

    [Fact]
    public void AnUnknownNamingPolicyIsNamed() =>
        Assert.Contains("'renameAll' is one of", SingleError("@derive(Serializable)\n@serialize(renameAll: .Shouting)\nstruct S {\n    var a: Int\n}"));

    [Fact]
    public void ATagIsForAUnion() =>
        Assert.Contains("'S' is not a union", SingleError("@derive(Serializable)\n@serialize(tag: \"t\")\nstruct S {\n    var a: Int\n}"));

    [Fact]
    public void ContentGoesWithATag() =>
        Assert.Contains("goes with 'tag:'", SingleError("@derive(Serializable)\n@serialize(content: \"c\")\nunion U {\n    A(Int)\n}"));

    [Fact]
    public void AnInternallyTaggedPayloadIsLabelled() =>
        Assert.Contains("'A' has one without", SingleError("@derive(Serializable)\n@serialize(tag: \"t\")\nunion U {\n    A(Int)\n}"));

    [Fact]
    public void ARenameIsAStringLiteral() =>
        Assert.Contains("string literal", SingleError("@derive(Serializable)\nstruct S {\n    @serialize(rename: 1)\n    var a: Int\n}"));

    [Fact]
    public void ACaseIsNeverSkipped() =>
        Assert.Contains("never skipped", SingleError("@derive(Serializable)\nunion U {\n    @serialize(skip)\n    A(Int)\n}"));

    [Fact]
    public void AnUnknownOptionIsNamed() =>
        Assert.Contains("takes 'rename:'", SingleError("@derive(Serializable)\nstruct S {\n    @serialize(hidden)\n    var a: Int\n}"));

    [Fact]
    public void ASkippedOrDefaultedFieldNeedsAnInitialValue() {
        Assert.Contains("'a' is skipped", SingleError("@derive(Serializable)\nstruct S {\n    @serialize(skip)\n    var a: Int\n}"));
        Assert.Contains("'a' is defaulted", SingleError("@derive(Serializable)\nstruct S {\n    @serialize(defaulted)\n    var a: Int\n}"));
    }

    [Theory]
    [InlineData("Int", "a single value")]
    [InlineData("String", "a single value")]
    [InlineData("Int[>..]", "an array")]
    [InlineData("Inner?", "an optional")]
    [InlineData("Tagged", "a union without a 'tag:'")]
    [InlineData("E", "an enum")]
    public void WhatIsNoStructIsNotFlattened(string type, string why) =>
        Assert.Contains(
            why,
            SingleError(
                "@derive(Serializable)\nstruct Inner {\n    var b: Int\n}\n@derive(Serializable)\nenum E {\n    A\n}\n"
                + "@derive(Serializable)\nunion Tagged {\n    A(b: Int)\n}\n"
                + $"@derive(Serializable)\nstruct S {{\n    @serialize(flatten)\n    var a: {type}\n}}"
            )
        );

    [Theory]
    [InlineData("rename: \"x\"")]
    [InlineData("skip")]
    [InlineData("defaulted")]
    public void AFlattenedFieldHasNoKey(string option) =>
        Assert.Contains(
            "no key of its own",
            SingleError($"@derive(Serializable)\nstruct I {{\n    var b: Int\n}}\n@derive(Serializable)\nstruct S {{\n    @serialize(flatten, {option})\n    var a: I = I(b: 1)\n}}")
        );

    [Fact]
    public void AFlattenedKeyIsNotRepeated() =>
        Assert.Contains(
            "the key \"b\" twice",
            SingleError("@derive(Serializable)\nstruct I {\n    var b: Int\n}\n@derive(Serializable)\nstruct S {\n    var b: Int\n    @serialize(flatten)\n    var a: I\n}")
        );

    [Fact]
    public void ACaseIsNotFlattened() =>
        Assert.Contains("'flatten' is for a field", SingleError("@derive(Serializable)\nunion U {\n    @serialize(flatten)\n    A(Int)\n}"));

    [Fact]
    public void UntaggedIsForAUnion() =>
        Assert.Contains("'S' is not a union", SingleError("@derive(Serializable)\n@serialize(untagged)\nstruct S {\n    var a: Int\n}"));

    [Fact]
    public void UntaggedHasNoTag() =>
        Assert.Contains("leaves the tag out", SingleError("@derive(Serializable)\n@serialize(untagged, tag: \"t\")\nunion U {\n    A(b: Int)\n}"));

    [Fact]
    public void AnUntaggedCaseIsNotRenamed() =>
        Assert.Contains("nothing to rename", SingleError("@derive(Serializable)\n@serialize(untagged)\nunion U {\n    @serialize(rename: \"x\")\n    A(Int)\n}"));

    [Fact]
    public void OneUntaggedCaseIsNull() =>
        Assert.Contains("'C' would never be read back", SingleError("@derive(Serializable)\n@serialize(untagged)\nunion U {\n    A(Int)\n    B\n    C\n}"));
}
