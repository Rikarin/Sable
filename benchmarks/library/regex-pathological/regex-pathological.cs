using System.Text.RegularExpressions;

// Port of regex-pathological.sb. A pattern that takes a backtracker time
// exponential in the length of each line it fails on: .NET's linear-time
// engine, as Sable's is.
[Benchmark("library/regex-pathological")]
static class LibraryRegexPathological {
    public static Func<string> Setup() {
        var text = string.Concat(Enumerable.Repeat("aaaaaaaaaaaaaaaaaaaa!\n", 1000));
        var regex = new Regex(@"(?m)^(a|aa)+$", RegexOptions.NonBacktracking);
        return () => Run(regex, text);
    }

    static string Run(Regex regex, string text) {
        checked {
            long count = 0;
            for (var round = 0; round < 10; round++) {
                count += regex.Count(text);
            }

            return $"{count} {text.Length}";
        }
    }
}
