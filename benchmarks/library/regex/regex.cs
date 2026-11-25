using System.Text.RegularExpressions;

// Port of regex.sb: each pattern's matches counted in a megabyte of text.
[Benchmark("library/regex", "words")]
[Benchmark("library/regex", "literal")]
[Benchmark("library/regex", "alternation")]
[Benchmark("library/regex", "email")]
static class LibraryRegex {
    public static Func<string> Setup(string pattern) {
        var text = RegexSample.Text();
        var regex = new Regex(pattern switch {
            "words" => @"\w+",
            "literal" => "Sherlock Holmes",
            "alternation" => "Sherlock|Holmes|Watson|Irene|Adler",
            _ => @"[\w.+-]+@[\w-]+\.[\w.-]+",
        }, RegexOptions.Compiled);
        return () => Run(regex, text);
    }

    static string Run(Regex regex, string text) {
        checked {
            long count = 0;
            for (var round = 0; round < 10; round++) {
                count += regex.Count(text);
            }

            return $"{count} {System.Text.Encoding.UTF8.GetByteCount(text)}";
        }
    }
}
