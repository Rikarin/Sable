// Port of url.sb, with Uri.
[Benchmark("library/url")]
static class LibraryUrl {
    public static string Run() {
        checked {
            long total = 0;
            for (var i = 0; i < 100000; i++) {
                var url = new Uri($"https://user@www.example.com:8443/a/path/{i}/page.html?name=value&n={i}#top");
                total += url.Host.Length + url.AbsolutePath.Length + url.Query.Length + url.Port;
            }

            return total.ToString();
        }
    }
}
