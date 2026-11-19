namespace Sable.SIL.Tests;

/// <summary>
///     <c>@builtin</c> members, lowered: the builtin in place at each call,
///     with no function made for it, and a declaration that disagrees with
///     the builtin it names reported where it is written.
/// </summary>
public sealed class BuiltinLoweringTests {
    static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(params string[] sources) {
        var compilation = Compilation.Create(sources);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        return SilGen.Lower(compilation);
    }

    static Diagnostic Single(ErrorCode code, string core) {
        var (_, diagnostics) = Lower($"module Sable.Core\n{core}", "module M");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(code, diagnostic.Code);
        return diagnostic;
    }

    [Fact]
    public void ACallIsTheBuiltinInPlace() {
        var (module, diagnostics) = Lower(
            """
            module M
            fn tidy(_ s: String) -> Int {
                print(s)
                return s.trimmed().count
            }
            """
        );
        Assert.Empty(diagnostics);

        var builtins = module.Find("M.tidy")!.Blocks.SelectMany(static b => b.Instructions).OfType<BuiltinInst>().Select(static b => b.Name).ToList();
        Assert.Equal(["print_String", "string_trimmed", "string_count"], builtins);

        // Nothing is generated for a builtin member: there is nothing to call.
        Assert.DoesNotContain(module.Functions, static f => f.Name.EndsWith("String.trimmed", StringComparison.Ordinal) || f.Name.Contains(".print", StringComparison.Ordinal));
        Assert.Equal(3L, new SilInterpreter(module).Run("M.tidy", "  abc "));
    }

    /// <summary>
    ///     Two characters order by an instruction, as integers do — not by
    ///     Sable.Core's 'operator &lt;' on Char, whose body is the same
    ///     comparison and so used to call itself forever.
    /// </summary>
    [Fact]
    public void CharactersAreOrderedByTheBuiltin() {
        var (module, diagnostics) = Lower("module M\nfn probe(_ a: Char, _ b: Char) -> Bool { return a < b || a >= b }");
        Assert.Empty(diagnostics);

        var probe = module.Find("M.probe")!;
        var instructions = probe.Blocks.SelectMany(static b => b.Instructions).ToList();
        Assert.Contains(instructions.OfType<BuiltinInst>(), static b => b.Name == "cmp_lt_Char");
        Assert.Contains(instructions.OfType<BuiltinInst>(), static b => b.Name == "cmp_ge_Char");
        Assert.Empty(instructions.OfType<ApplyInst>());
        Assert.Equal(true, new SilInterpreter(module).Run("M.probe", new System.Text.Rune('a'), new System.Text.Rune('b')));
    }

    [Fact]
    public void ANameNoBuiltinHasIsAnError() {
        var error = Single(ErrorCode.ERR_UnknownBuiltin, "extension String {\n    @builtin(\"string_reversed\")\n    public fn reversed() -> String\n}");
        Assert.Contains("'string_reversed'", error.Message);
    }

    [Theory]
    [InlineData("extension String {\n    @builtin(\"string_trimmed\")\n    public fn tidied() -> Int\n}", "(String) -> String")]
    [InlineData("extension String {\n    @builtin(\"string_at\")\n    public fn at(index: Int, again: Int) -> Char\n}", "(String, Int) -> Char")]
    [InlineData("extension String {\n    @builtin(\"string_at\")\n    public static fn at(index: Int) -> Char\n}", "(String, Int) -> Char")]
    [InlineData("extension Char {\n    @builtin(\"string_count\")\n    public var size: Int { get }\n}", "(String) -> Int")]
    [InlineData("extension String {\n    @builtin(\"string_concat\")\n    public fn joining(other: inout String) -> String\n}", "other than by value")]
    public void ADeclarationThatDisagreesWithItsBuiltinIsAnError(string core, string message) {
        var error = Single(ErrorCode.ERR_BuiltinSignatureMismatch, core);
        Assert.Contains(message, error.Message);
    }

    static List<string> BuiltinsOf(SilFunction function) =>
        [..function.Blocks.SelectMany(static b => b.Instructions).OfType<BuiltinInst>().Select(static b => b.Name)];

    /// <summary>
    ///     A builtin family's member is, at each use, the builtin of the type
    ///     Self stands for there: on a concrete type, and in each instance of
    ///     generic code — with nothing called, as for any builtin.
    /// </summary>
    [Fact]
    public void AFamilysMemberIsTheBuiltinOfTheTypeSelfStandsFor() {
        var (module, diagnostics) = Lower(
            """
            module M
            fn zeros<T: FixedWidthInteger>(_ x: T) -> Int { return x.leadingZeroCount }
            fn probe(_ x: I8, _ y: U16) -> Int { return x.leadingZeroCount + zeros(y) + x.rotatedLeft(by: 3).popCount }
            """
        );
        Assert.Empty(diagnostics);
        Assert.Equal(["count_leading_zeros_I8", "rotate_left_I8", "count_ones_I8"], BuiltinsOf(module.Find("M.probe")!).Where(static n => !n.StartsWith("add_", StringComparison.Ordinal)));
        var instance = module.Find("M.zeros$of$U16")!;
        Assert.Equal(["count_leading_zeros_U16"], BuiltinsOf(instance));
        Assert.Empty(instance.Blocks.SelectMany(static b => b.Instructions).OfType<ApplyInst>());
        Assert.Equal(7L + 0L + 1L, new SilInterpreter(module).Run("M.probe", 1L, 0x8000L));
    }

    /// <summary>
    ///     Generic arithmetic over the numeric protocols costs what concrete
    ///     arithmetic does: monomorphised, a sum over a T is the instruction,
    ///     with no call to an operator.
    /// </summary>
    [Fact]
    public void GenericArithmeticIsTheInstructionOnceMonomorphised() {
        var (module, diagnostics) = Lower(
            """
            module M
            fn total<T: Numeric>(_ a: T, _ b: T) -> T { return a * b + a - T.one }
            fn probe() -> F32 { return F32(total(3, 4)) + total(1.5 as F32, 2.0) }
            """
        );
        Assert.Empty(diagnostics);
        foreach (var type in new[] { "Int", "F32" }) {
            var instance = module.Find($"M.total$of${type}")!;
            Assert.Equal([$"mul_{type}", $"add_{type}", $"sub_{type}"], BuiltinsOf(instance));
            // The one call is to 'one', a static property's getter, which LLVM inlines.
            Assert.DoesNotContain(instance.Blocks.SelectMany(static b => b.Instructions).OfType<ApplyInst>(),
                static a => a.Callee is not FunctionRefInst { Function: var name } || !name.Contains(".one", StringComparison.Ordinal));
        }

        Assert.Equal(14f + 3.5f, new SilInterpreter(module).Run("M.probe"));
    }

    /// <summary>
    ///     A family is checked against each built-in type that conforms to
    ///     its protocol, as any builtin is against its one.
    /// </summary>
    [Fact]
    public void AFamilyWithoutABuiltinForAConformingTypeIsAnError() {
        var error = Single(
            ErrorCode.ERR_UnknownBuiltin,
            """
            public protocol Rooted {
                fn root() -> Self
            }
            extension Rooted {
                @builtin("sqrt_Self")
                public fn root() -> Self
            }
            extension I8: Rooted { }
            """
        );
        Assert.Contains("'sqrt_I8'", error.Message);
    }

    [Fact]
    public void AFamilyMeetingNoRequirementIsAnError() {
        var error = Single(
            ErrorCode.ERR_MalformedBuiltin,
            """
            public protocol Rooted { }
            extension Rooted {
                @builtin("sqrt_Self")
                public fn root() -> Self
            }
            extension F64: Rooted { }
            """
        );
        Assert.Contains("meets none", error.Message);
    }
}
