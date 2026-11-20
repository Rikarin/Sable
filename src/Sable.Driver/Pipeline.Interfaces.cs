using Sable.CodeGen;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     Compiling a program against its libraries' interfaces (docs/05 §5.4,
///     #182): what a build that keeps each library's <c>.sbi</c> asks. The
///     program's own files are checked and lowered; a library's bodies are not
///     bound or checked again, and its code is taken from its interface's SIL
///     rather than lowered.
/// </summary>
public static partial class Pipeline {
    static readonly Lazy<string> Identity = new(static () =>
        LibraryInterface.IdentityOf([..LibraryInterface.FrontEnd, typeof(LlvmEmitter).Assembly, typeof(Pipeline).Assembly]));

    /// <summary>
    ///     This compiler's identity, as an interface it writes records it
    ///     (<see cref="LibraryInterface.IdentityOf" />): a hash of its own
    ///     build, so that no other build of it reads what this one wrote.
    /// </summary>
    public static string CompilerIdentity => Identity.Value;

    /// <summary>
    ///     <c>Sable.Core</c>'s interface for files parsed with
    ///     <paramref name="options" /> — the target and profile they name —
    ///     built from the core's source embedded in the compiler.
    /// </summary>
    /// <exception cref="InvalidOperationException">The core does not compile: the compiler's own bug.</exception>
    public static LibraryInterface CoreInterface(ParseOptions options, bool denyReflection = false) {
        var compilation = Compilation.Create([], options);
        var (built, errors) = LibraryInterface.Build(compilation, CoreLibrary.ModuleName, CoreLibrary.Contains, CompilerIdentity, denyReflection);
        return built ?? throw new InvalidOperationException(
            $"Sable.Core does not compile: {string.Join("; ", errors.Select(static e => $"{e.Diagnostic.Id} {e.Diagnostic.Message}"))}");
    }

    /// <summary>
    ///     <see cref="Compile(IReadOnlyList{SyntaxTree}, string?, bool, BuildTimings?, bool, NativeTarget?, bool, bool, bool, string?, bool)" />
    ///     against the interfaces of the libraries it uses: the program's files
    ///     — and the libraries no interface is given for — are checked and
    ///     lowered as a unit of a program compiled separately, and each
    ///     interface's SIL is linked into what that makes. What it reports,
    ///     and what it builds, is what compiling the whole from source would.
    /// </summary>
    /// <remarks>
    ///     The libraries' declarations are still declared from their source
    ///     (which ships with the compiler and the packages, and holds the
    ///     generic bodies the program instantiates); what is skipped is
    ///     binding and checking their bodies and lowering their code. An
    ///     interface this compilation cannot use — built from other files,
    ///     for other settings, or with other extensions of its types — is an
    ///     error, which a build answers by building it again.
    /// </remarks>
    public static CompileResult CompileAgainst(
        IReadOnlyList<SyntaxTree> program,
        IReadOnlyList<LibraryInterface> interfaces,
        string? debugDirectory = null,
        bool checkExclusivity = true,
        BuildTimings? timings = null,
        bool optimised = false,
        NativeTarget? target = null,
        bool checkOverflow = true,
        bool denyReflection = false,
        string? optLevel = null,
        bool checkArc = false
    ) {
        Compilation compilation;
        List<string> errors;
        List<string> warnings;
        bool Covered(SyntaxTree tree) => interfaces.Any(i => i.Covers(tree));
        using (BuildTimings.Of(timings, "bind")) {
            compilation = CompilationOf(program);
            var settings = LibraryInterface.SettingsOf(program.FirstOrDefault()?.Options ?? compilation.Options, denyReflection);
            foreach (var library in interfaces) {
                if (library.Mismatch(compilation, settings) is { } why) {
                    return new(null, null, [$"error: cannot compile against the interface of '{library.Unit}': {why}"]);
                }
            }

            var diagnostics = compilation.GetAllDiagnosticsByFile(tree => !Covered(tree));
            errors = Formatted(diagnostics, program);
            warnings = Warnings(diagnostics, program);
        }

        var level = optLevel ?? (optimised ? "2" : "0");
        return CompileChecked(compilation, program, errors, debugDirectory, checkExclusivity, timings, level, target, checkOverflow, false, denyReflection, checkArc,
            lower: c => LowerAgainst(c, interfaces, denyReflection)) with { Warnings = warnings };
    }

    /// <summary>
    ///     The program lowered as the unit of every file no interface covers —
    ///     the unit holding the entry point, which sets up every global — with
    ///     each interface's SIL linked in: one module, as a whole program's.
    /// </summary>
    static (SilModule, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)>) LowerAgainst(
        Compilation compilation,
        IReadOnlyList<LibraryInterface> interfaces,
        bool denyReflection
    ) {
        var (module, diagnostics) = SilGen.LowerUnitByFile(compilation, new SilUnit(tree => !interfaces.Any(i => i.Covers(tree))), SilProgram.Index(compilation), denyReflection);
        foreach (var library in interfaces) {
            library.LinkInto(module);
        }

        module.Unit = null;
        return (module, diagnostics);
    }

    /// <summary>The program's own warnings among diagnostics already gathered.</summary>
    static List<string> Warnings(IEnumerable<(Diagnostic Diagnostic, SyntaxTree? Tree)> diagnostics, IReadOnlyList<SyntaxTree> program) {
        var order = program.Select(static (tree, index) => (tree, index)).ToDictionary(static p => p.tree, static p => p.index);
        return [
            ..diagnostics
                .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Warning)
                .Where(d => d.Tree is { } t && order.ContainsKey(t))
                .OrderBy(d => order[d.Tree!])
                .ThenBy(static d => d.Diagnostic.Span.Start)
                .Select(static d => d.Diagnostic.Format(d.Tree!.Text)),
        ];
    }
}
