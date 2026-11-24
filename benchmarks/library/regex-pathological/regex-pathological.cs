using System.Text.RegularExpressions;

// A pattern that takes a backtracker time exponential in the length of each
// line it fails on: .NET's linear-time engine, as Sable's is.
[Benchmark("library/regex-pathological")]
static class LibraryRegexPathological {
    public static string Run() {
        checked {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 100; i++) {
                builder.Append("aaaaaaaaaaaaaaaaaaaa!\n");
            }

            var text = builder.ToString();
            var regex = new Regex(@"(?m)^(a|aa)+$", RegexOptions.NonBacktracking);
            long count = 0;
            for (var round = 0; round < 10; round++) {
                count += regex.Count(text);
            }

            return $"{count} {text.Length}";
        }
    }
}
