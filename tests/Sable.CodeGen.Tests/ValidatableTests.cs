using Sable.Driver;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>@derive(Validatable)</c> (docs/06 §6.5): the rules a struct's
///     attributes make, native and interpreted, and every wrong one reported
///     where it is written.
/// </summary>
public sealed class ValidatableTests {
    static IReadOnlyList<string> Errors(string declarations, string imports = "import Sable.Validation") =>
        Pipeline.Compile(new SourceText($"module M\n{imports}\n\n{declarations}\n\n@main\nfn main() {{ }}\n", "test.sb")).Errors;

    static string SingleError(string declarations, string imports = "import Sable.Validation") {
        var errors = Errors(declarations, imports);
        Assert.True(errors.Count == 1, string.Join("\n", errors));
        return errors[0];
    }

    [Fact]
    [Native]
    public void AttributesAreRules() =>
        Assert.Equal(
            """
            email: 'Email' must not be empty. [NotEmpty]
            email: 'Email' is not a valid email address. [Email]
            age: You must be an adult. [GreaterThanOrEqual]
            again: 'Again' must be equal to . [Equal]
            kind: 'Kind' is no slug. [Matches]
            places[1].city: 'City' must not be empty. [NotEmpty]
            taken [Must]

            """.ReplaceLineEndings("\n"),
            AssertAgree("""
                module P

                import Sable.Validation

                extension FieldRules where V == String {
                    @discardable
                    fn slug() -> FieldRules<T, V> {
                        return matches("^[a-z]+$").message("'{PropertyName}' is no slug.")
                    }
                }

                @derive(Validatable)
                struct Place {
                    @notEmpty
                    var city: String
                }

                @derive(Validatable)
                struct Signup {
                    @notEmpty @email
                    var email: String
                    @range(18...) @message("You must be an adult.")
                    var age: Int
                    @equal(\.email)
                    var again: String
                    @slug
                    var kind: String?
                    var places: Place[>..]

                    static fn moreRules(_ r: inout Rules<Signup>) {
                        r.each(\.places).validated()
                        r.must { s in await isFree(s) }.message("taken")
                    }
                }

                async fn isFree(_ s: Signup) -> Bool {
                    await Task.yield()
                    return s.age > 20
                }

                @main
                async fn main() {
                    let bad = Signup(email: "", age: 3, again: "x", kind: "A", places: [Place(city: "x"), Place(city: "")])
                    for failure in (await bad.validate()).failures {
                        print("\(failure.path)\(failure.path.isEmpty ? "" : ": ")\(failure.message) [\(failure.code)]")
                    }
                }
                """).Stdout
        );

    [Fact]
    public void ARuleThereIsNotIsReportedAtTheAttribute() {
        var error = SingleError("@derive(Validatable)\nstruct S {\n    @emial\n    var x: String\n}");
        Assert.Contains("S3092", error);
        Assert.Contains("'@emial'", error);
        Assert.Contains("test.sb:6:6", error);
    }

    [Fact]
    public void ARuleThatDoesNotFitTheFieldIsReported() {
        Assert.Contains("S3092", SingleError("@derive(Validatable)\nstruct S {\n    @email\n    var x: Int\n}"));
        Assert.Contains("S3007", SingleError("@derive(Validatable)\nstruct S {\n    @range(\"a\")\n    var x: Int\n}"));
        Assert.Contains("S2002", SingleError("@derive(Validatable)\nstruct S {\n    @equal(\\.why)\n    var x: Int\n}"));
    }

    [Fact]
    public void AnAttributeOfATypeThatDoesNotDeriveIsNone() =>
        Assert.Contains("there is no attribute '@email'", SingleError("struct S {\n    @email\n    var x: String\n}"));

    [Fact]
    public void OnlyAStructDerives() {
        Assert.Contains("S3071", SingleError("@derive(Validatable)\nunion U {\n    A\n}"));
        Assert.Contains("S3071", SingleError("@derive(Validatable)\nclass C {\n    init() { }\n}"));
    }

    [Fact]
    public void ItsModuleIsImported() =>
        Assert.Contains("'import Sable.Validation'", SingleError("@derive(Validatable)\nstruct S {\n    var x: Int\n}", ""));

    [Fact]
    public void RulesWrittenByHandAreNotDerived() =>
        Assert.Contains("S3071", SingleError("@derive(Validatable)\nstruct S {\n    var x: Int\n    static fn rules(_ r: inout Rules<S>) { }\n}"));
}
