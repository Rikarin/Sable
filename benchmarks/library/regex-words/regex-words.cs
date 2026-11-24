using System.Text.RegularExpressions;

// The pattern's matches counted in a megabyte of text.
[Benchmark("library/regex-words")]
static class LibraryRegexWords {
    public static string Run() => RegexSample.Count(@"\w+", RegexOptions.Compiled);
}
