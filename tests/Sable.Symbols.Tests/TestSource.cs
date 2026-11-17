using System.Text;

namespace Sable.Symbols.Tests;

/// <summary>Builds the little programs the binding tests check.</summary>
internal static class TestSource {
    /// <summary>
    ///     Wraps a body in a function, lifting its leading "assume a value of
    ///     this type" declarations into parameters.
    /// </summary>
    /// <remarks>
    ///     A fixture often needs a value of some type without caring where it
    ///     came from, and writes <c>let s: Shape</c> for it. That is a local
    ///     nothing assigns, which definite assignment is right to reject, so the
    ///     declaration becomes a parameter instead — same meaning, and the body
    ///     stays readable.
    /// </remarks>
    public static string Probe(string body, string extra = "", string moduleName = "M") {
        var parameters = new StringBuilder();
        var statements = new StringBuilder();

        foreach (var line in body.Split('\n')) {
            if (AssumedValue(line) is { } assumed) {
                if (parameters.Length > 0) {
                    parameters.Append(", ");
                }

                parameters.Append("_ ").Append(assumed.Name).Append(": ").Append(assumed.Type);
                continue;
            }

            statements.Append(line).Append('\n');
        }

        return $"module {moduleName}\n{extra}\nfn probe({parameters}) {{\n{statements}}}";
    }

    /// <summary>Reads <c>let name: Type</c> with no initialiser, or null.</summary>
    static (string Name, string Type)? AssumedValue(string line) {
        var text = line.Trim();
        if (!text.StartsWith("let ", StringComparison.Ordinal)) {
            return null;
        }

        if (text.Contains('=', StringComparison.Ordinal)) {
            return null;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) {
            return null;
        }

        var name = text[4..colon].Trim();
        var type = text[(colon + 1)..].Trim();
        if (name.Length == 0 || type.Length == 0) {
            return null;
        }

        if (name.Contains(' ', StringComparison.Ordinal)) {
            return null;
        }

        return (name, type);
    }
}
