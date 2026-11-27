using System.Text.RegularExpressions;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Symbols shortened by substitutions (#300, <see cref="SymbolNames" />):
///     what they are written as, read back as their SIL names, and the
///     runtime's tables the same as the compiler's.
/// </summary>
public sealed partial class SymbolNamesTests {
    [Theory]
    [InlineData("Main.main", "Main.main")]
    [InlineData("Sable.Core.Array.append", "~a.Array.append")]
    [InlineData("witnesses.Sable.Core.ClosedRange$of$Int.Sable.Core.Collection", "~A.~a.ClosedRange$of$~M.~a.Collection")]
    [InlineData("$convert$$thick$$Ssg.Page$$Ssg.Page$$$$$Bool", "$~V$$~P$$Ssg.Page$$~0_$$$$$~O")]
    [InlineData("Sable.Math.Geometry.Vector.length", "~g.Geometry.Vector.length")]
    [InlineData("Main.~", "Main.~~")]
    [InlineData("cache.53", "~I.53")]
    public void ANameIsWrittenWithItsSubstitutions(string name, string symbol) {
        Assert.Equal(symbol, SymbolNames.Mangle(name));
        Assert.Equal(name, SymbolNames.Demangle(symbol));
    }

    [Theory]
    [InlineData("Main.Point.~~$prefix")]
    [InlineData("~Sable.Core.~0_")]
    [InlineData("Größe.Größe.weiß")]
    [InlineData("a.b.c.abcd.abcd.abcd$abcd.efgh$abcd.efgh.x")]
    [InlineData("Sable.Core.Dictionary$of$Sable.Core.String$Sable.Core.Dictionary$of$Sable.Core.String$Int.Sable.Core.Dictionary")]
    [InlineData("M.$generic$witness$M.Box$of$$opaque$T$M.P$0$f.cold.1")]
    [InlineData("_._.__.x9.9x.__init__")]
    [InlineData("")]
    public void ASymbolIsReadBackAsItsName(string name) =>
        Assert.Equal(name, SymbolNames.Demangle(SymbolNames.Mangle(name)));

    /// <summary>Every symbol of a program that uses the library's generic code, read back, and no two the same.</summary>
    [Fact]
    public void EveryFunctionAndRecordOfAProgramHasASymbolOfItsOwn() {
        var source = """
                     module Words

                     protocol Shape { fn area() -> F64 }
                     struct Square: Shape { let side: F64
                         fn area() -> F64 { return side * side } }

                     @main
                     fn main() {
                         var counts: [String: Int] = [:]
                         for word in ["a", "b", "a", "c"] {
                             counts[word, default: 0] += 1
                         }
                         let shapes: (any Shape)[>..] = [Square { side: 2.0 }]
                         var sorted = [3, 1, 2]
                         sorted.sort()
                         print("\(counts.count) \(shapes[0].area()) \(sorted)")
                     }
                     """;
        var result = Pipeline.Compile(new SourceText(source, "words.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var symbols = Symbol().Matches(result.LlvmIr!).Select(static m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(symbols, static s => s.Contains('~', StringComparison.Ordinal));
        var names = symbols.Select(SymbolNames.Demangle).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in names) {
            Assert.Equal(name, SymbolNames.Demangle(SymbolNames.Mangle(name)));
            Assert.Equal(SymbolNames.Mangle(name), symbols.Single(s => SymbolNames.Demangle(s) == name));
        }
    }

    /// <summary>The runtime reads a backtrace's symbols back with the same substitutions as the compiler writes.</summary>
    [Fact]
    public void TheRuntimesTablesAreTheCompilers() {
        var runtime = File.ReadAllText(Path.Combine(DocExampleTests.RepositoryRoot, "runtime", "symbols.c"));
        string[] Table(string name) {
            var body = Regex.Match(runtime, $@"{name}\[26\] = \{{(.*?)\}};", RegexOptions.Singleline).Groups[1].Value;
            return Regex.Matches(body, "\"([^\"]*)\"|NULL").Select(static m => m.Groups[1].Success ? m.Groups[1].Value : "").ToArray();
        }

        var modules = Table("symbol_modules");
        var words = Table("symbol_words");
        Assert.Equal(26, modules.Length);
        Assert.Equal(26, words.Length);
        for (var i = 0; i < 26; i++) {
            var module = SymbolNames.Modules.Where(m => m.Code == (char)('a' + i)).Select(static m => m.Text).SingleOrDefault() ?? "";
            var word = SymbolNames.Words.Where(m => m.Code == (char)('A' + i)).Select(static m => m.Text).SingleOrDefault() ?? "";
            Assert.Equal(module, modules[i]);
            Assert.Equal(word, words[i]);
        }
    }

    [GeneratedRegex("@\"sable\\.([^\"]*)\"")]
    private static partial Regex Symbol();
}
