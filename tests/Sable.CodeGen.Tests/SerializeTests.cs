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
        Assert.Contains("takes 'rename:'", SingleError("@derive(Serializable)\nstruct S {\n    @serialize(flatten)\n    var a: Int\n}"));

    [Fact]
    public void ASkippedOrDefaultedFieldNeedsAnInitialValue() {
        Assert.Contains("'a' is skipped", SingleError("@derive(Serializable)\nstruct S {\n    @serialize(skip)\n    var a: Int\n}"));
        Assert.Contains("'a' is defaulted", SingleError("@derive(Serializable)\nstruct S {\n    @serialize(defaulted)\n    var a: Int\n}"));
    }
}
