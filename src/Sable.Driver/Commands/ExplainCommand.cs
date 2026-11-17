namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable explain &lt;S1234&gt;</c>: the long-form documentation of a
///     diagnostic (docs/05 §5.1), from <c>docs/diagnostics/</c>.
/// </summary>
public sealed class ExplainCommand : Command {
    public override string Name => "explain";
    public override string Summary => "print the full documentation for a diagnostic";

    public override string Usage => """
        usage: sable explain <code>
               sable explain --list

          <code>   a diagnostic's code, as the compiler prints it: S3004 (or 3004)
          --list   every code that has documentation, with its title
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments.Count != 1) {
            return BadUsage();
        }

        if (arguments[0] == "--list") {
            foreach (var documented in DiagnosticDocs.Codes) {
                Console.WriteLine($"{documented}  {DiagnosticDocs.Title(DiagnosticDocs.Page(documented)!)}");
            }

            return 0;
        }

        if (DiagnosticDocs.Normalise(arguments[0]) is not { } code) {
            Console.Error.WriteLine($"sable explain: '{arguments[0]}' is not a diagnostic code; codes look like S3004");
            return 2;
        }

        if (DiagnosticDocs.Page(code) is { } page) {
            Console.Write(page);
            return 0;
        }

        if (DiagnosticDocs.Known(code) is { } known) {
            Console.Error.WriteLine($"sable explain: {code} ({known}) has no long-form documentation yet; its message says what it found");
            return 1;
        }

        Console.Error.WriteLine($"sable explain: the compiler has no diagnostic {code}{Nearby(code)}");
        Console.Error.WriteLine("codes run S0001-S0999 lexical, S1000-S1999 syntax, S2000-S2999 names, S3000-S3999 types,");
        Console.Error.WriteLine("S6000-S6999 patterns and flow, S7000-S7999 code generation; 'sable explain --list' lists them");
        return 1;
    }

    /// <summary>The documented codes closest to a mistyped one, as a hint.</summary>
    static string Nearby(string code) {
        var near = DiagnosticDocs.Codes
            .Where(c => Distance(c, code) == 1)
            .Take(3)
            .ToList();
        return near.Count == 0 ? "" : $"; did you mean {string.Join(" or ", near)}?";
    }

    static int Distance(string a, string b) => a.Length != b.Length ? int.MaxValue : a.Zip(b).Count(static p => p.First != p.Second);
}
