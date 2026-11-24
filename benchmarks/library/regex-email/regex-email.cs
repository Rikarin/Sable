using System.Text.RegularExpressions;

// The pattern's matches counted in a megabyte of text.
[Benchmark("library/regex-email")]
static class LibraryRegexEmail {
    public static string Run() => RegexSample.Count(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);
}
