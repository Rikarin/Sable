using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Fuzz;

/// <summary>
///     A library's interface (<c>.sbi</c>, #182), as a build reads one it
///     finds on disk: any bytes are either refused — <see cref="LibraryInterface.Read" />
///     gives null, <see cref="SymbolInterface.Read" /> throws
///     <see cref="InvalidDataException" /> — or read; and symbols that read
///     are written again as bytes that read back to the same symbols, which
///     is what keeps an interface the reader accepted from meaning two things.
/// </summary>
/// <remarks>
///     The input is a symbol section; the same bytes are tried as a whole
///     file too, which the file's own hash nearly always refuses — what is
///     tested there is that nothing past the header is trusted before it is.
///     The symbols are read against the core's, read back from its own
///     interface, so that a section naming the core's types resolves them.
/// </remarks>
public sealed class InterfaceTarget : Target {
    public override string Name => "interface";

    public override int MaxLength => 256 * 1024;

    public override TimeSpan Limit => TimeSpan.FromSeconds(10);

    static readonly Lazy<LibraryInterface> Core = new(static () => {
        var (built, _) = LibraryInterface.Build(Compilation.Create([]), CoreLibrary.ModuleName, CoreLibrary.Contains, "fuzz");
        return built ?? throw new InvalidOperationException("Sable.Core does not compile");
    });

    public override void Run(ReadOnlySpan<byte> data) {
        _ = LibraryInterface.Read(data, "fuzz");

        IReadOnlyList<ModuleSymbol> modules;
        var core = Core.Value.ReadSymbols();
        try {
            modules = SymbolInterface.Read(data, core);
        } catch (InvalidDataException) {
            return;
        }

        byte[] written;
        try {
            written = SymbolInterface.Write(modules, core);
        } catch (InvalidOperationException) {
            // Symbols no source could declare — a type kind the section's tags do not name — are the writer's to refuse.
            return;
        }

        IReadOnlyList<ModuleSymbol> again;
        try {
            again = SymbolInterface.Read(written, core = Core.Value.ReadSymbols());
        } catch (InvalidDataException failure) {
            throw new BrokenPromise($"symbols that read were written as bytes that do not: {failure.Message}");
        }

        if (!SymbolInterface.Write(again, core).AsSpan().SequenceEqual(written)) {
            throw new BrokenPromise("symbols that read were written as bytes that read back as other symbols");
        }
    }

    public override IEnumerable<(string Name, byte[] Data)> Seeds(string root) {
        yield return ("core", Core.Value.SymbolBytes);
        var program = Compilation.Create([SyntaxTree.Parse(new SourceText(
            "module App\n\npublic struct Box<T: Equatable> {\n    public var items: T[]\n    public fn first() -> T? => none\n}\n\nextension Int {\n    fn twice() -> Int => self * 2\n}\n"
            + "public union Shape {\n    case circle(Int)\n}\npublic enum Level: Int {\n    case low = 1\n}\n",
            "app.sb"))]);
        yield return ("app", SymbolInterface.Write([program.GetModule("App")!], program.Modules));
        yield return ("file", Core.Value.Write());
    }

    public override IEnumerable<string> Dictionary() => [
        "Sable.Core", "Int", "String", "Optional", "Array", "Self", "Element", "Equatable", "init", "inline", "(always)", "main", "MainActor",
        "\u0001", "\u0002", "\u0003", "\u0004", "\u0005", "\u0006", "\u0011", "\u0012", "ÿÿÿÿ", "\u0000\u0000\u0000\u0000", "SBIL",
    ];
}
