namespace Sable.SIL.Tests;

/// <summary>
///     A Substring (Sable.Core's Substring.sb): what a String's range
///     subscript gives, its string's storage shared rather than its bytes
///     copied. Counted in the interpreter's allocations, cutting a text into
///     pieces, comparing, hashing, searching, trimming and parsing them, and
///     looking one up in a dictionary of String keys, allocates nothing; only
///     <c>toString()</c> makes a string of a piece's bytes.
/// </summary>
public sealed class SubstringLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));

        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        SilPassManager.Run(module, SilPassManager.Default);
        return module;
    }

    /// <summary>What the function gives, and how many objects running it allocated.</summary>
    static (object? Result, int Allocations) Run(SilModule module, string function) {
        var interpreter = new SilInterpreter(module);
        var result = interpreter.Run($"M.{function}");
        Assert.True(interpreter.LiveObjects == 0, $"{interpreter.LiveObjects} object(s) leaked");
        return (result, interpreter.Allocations);
    }

    const string Text = """
                        fn text() -> String {
                            // Made at run time, so the pieces have a string of their own to share.
                            return "  width = 1280 ; height = 720 ; name = sable  ".appending("")
                        }
                        """;

    [Fact]
    public void ARangeOfAStringIsASubstringThatCopiesNothing() {
        var module = Lower(
            Text + """

                   fn whole() -> Int {
                       return text().utf8Count
                   }

                   fn pieces() -> Int {
                       let line = text()
                       var total = 0
                       var start = line.startIndex
                       while let semicolon = line[start...].firstIndex(of: ";") {
                           let field = line[start..<semicolon]
                           let equals = field.firstIndex(of: "=")!
                           let value = field[field.index(after: equals)...].trimmed()
                           total = total + value.toInt()!
                           start = line.index(after: semicolon)
                       }
                       let last = line[start...].trimmed()
                       if last.hasPrefix("name") && last.hasSuffix("sable") && last[^5...] == "sable" {
                           total = total + 1
                       }
                       return total
                   }
                   """
        );

        Assert.Contains("string_utf8_find_range", SilPrinter.Print(module), StringComparison.Ordinal);

        var (length, baseline) = Run(module, "whole");
        Assert.Equal(46L, length);

        // As many allocations as making the text takes, and not one more.
        var (total, allocations) = Run(module, "pieces");
        Assert.Equal(2001L, total);
        Assert.Equal(baseline, allocations);
    }

    [Fact]
    public void ASubstringIsLookedUpHashedAndParsedWithoutAString() {
        var module = Lower(
            Text + """

                   fn table() -> [String: Int] {
                       return ["width": 1, "height": 2, "name": 3]
                   }

                   fn made() -> Int {
                       let line = text()
                       let sizes = table()
                       return line.utf8Count + sizes.count
                   }

                   fn looked() -> Int {
                       let line = text()
                       let sizes = table()
                       var found = 0
                       for field in line.asSubstring.split(";") {
                           let key = field[..<field.firstIndex(of: "=")!].trimmed()
                           found = found * 10 + (sizes[key] ?? 0)
                       }
                       let width = line[line.index(atScalar: 10)..<line.index(atScalar: 14)]
                       if width.hashValue == "1280".hashValue && width == "1280" && "1280" == width {
                           found = found * 10 + (try! Int.parse(width)) % 7
                       }
                       return found
                   }

                   fn copied() -> String {
                       let line = text()
                       return line[line.index(atScalar: 10)..<line.index(atScalar: 14)].toString()
                   }
                   """
        );

        var (_, baseline) = Run(module, "made");

        // The array 'split' fills is the one allocation the lookups add.
        var (found, allocations) = Run(module, "looked");
        Assert.Equal(1236L, found);
        Assert.Equal(baseline + 1, allocations);

        // Copying a piece out is a string of its own.
        Assert.Equal("1280", Run(module, "copied").Result);
    }
}
