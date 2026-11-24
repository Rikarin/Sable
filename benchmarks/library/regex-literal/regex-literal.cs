using System.Text.RegularExpressions;

// The pattern's matches counted in a megabyte of text.
[Benchmark("library/regex-literal")]
static class LibraryRegexLiteral {
    public static string Run() => RegexSample.Count("Sherlock Holmes", RegexOptions.Compiled);
}
