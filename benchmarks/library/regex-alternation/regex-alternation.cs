using System.Text.RegularExpressions;

// The pattern's matches counted in a megabyte of text.
[Benchmark("library/regex-alternation")]
static class LibraryRegexAlternation {
    public static string Run() => RegexSample.Count("Sherlock|Holmes|Watson|Irene|Adler", RegexOptions.Compiled);
}
