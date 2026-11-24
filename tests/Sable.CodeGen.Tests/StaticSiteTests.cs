using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     docs/04 §4.4's "a real program ships": the static site generator in
///     examples/ssg, run on a copy of its sample site, natively and in the
///     interpreter. The two must say the same and write the same files, byte
///     for byte, and free everything they allocate.
/// </summary>
public sealed class StaticSiteTests {
    [Fact]
    public void TheGeneratorBuildsTheSampleSiteTheSameBothWays() {
        var root = Path.Combine(Path.GetTempPath(), $"sable-ssg-{Guid.NewGuid():N}");
        try {
            var site = Path.Combine(root, "site");
            CopyDirectory(Path.Combine(Root, "examples/ssg/site"), site);
            var output = Path.Combine(root, "out");

            // A page left over from an earlier build, which the generator removes.
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "stale.html"), "old");

            var written = new Dictionary<string, Dictionary<string, string>>();
            var run = AssertAgree(
                File.ReadAllText(Path.Combine(Root, "examples/ssg/ssg.sb")),
                stepLimit: 100_000_000,
                arguments: [site, output],
                afterEach: way => {
                    written[way] = Directory.GetFiles(output)
                        .ToDictionary(static f => Path.GetFileName(f), static f => File.ReadAllText(f));
                    // The second run starts from nothing, as the first did not.
                    Directory.Delete(output, recursive: true);
                }
            );

            Assert.Equal(
                """
                  building.html              1.5 KB   106 words, 5 heading(s), 1 code block(s)
                  memory.html                1.5 KB   126 words, 2 heading(s), 0 code block(s)
                  hello.html                 1.2 KB    72 words, 1 heading(s), 1 code block(s)
                  about.html                  628 B    26 words, 0 heading(s), 0 code block(s)
                  index.html                  966 B
                  tags.html                   874 B
                  style.css (copied)
                4 pages, 330 words, 4 tags, written to out

                """,
                run.Stdout
            );

            var interpreted = written["interpreter"];
            Assert.Equal(
                ["about.html", "building.html", "hello.html", "index.html", "memory.html", "style.css", "tags.html"],
                interpreted.Keys.Order(StringComparer.Ordinal)
            );

            // Natively too, where this run built it (SABLE_TEST_NATIVE=all, as CI sets).
            if (written.TryGetValue("native", out var native)) {
                Assert.Equal(interpreted.Keys.Order(StringComparer.Ordinal), native.Keys.Order(StringComparer.Ordinal));
                foreach (var (name, text) in interpreted) {
                    Assert.Equal(text, native[name]);
                }
            }

            // What the Markdown became, in the parts most likely to go wrong.
            var memory = interpreted["memory.html"];
            Assert.Contains("<title>Three tiers of memory · Sable Notes</title>", memory, StringComparison.Ordinal);
            Assert.Contains("<li><strong>Values</strong> live inline, copied on assignment.</li>", memory, StringComparison.Ordinal);
            Assert.Contains("<li>Many short-lived nodes — a parse tree, say:\n<ul>\n<li>allocate them in a region,</li>", memory, StringComparison.Ordinal);
            Assert.Contains("2 &lt; 3 &amp;&amp; &quot;quotes&quot; are &#39;fine&#39;, and *this* is not", memory, StringComparison.Ordinal);
            Assert.Contains("<a rel=\"prev\" href=\"building.html\">&larr; Building this site</a>", memory, StringComparison.Ordinal);

            var hello = interpreted["hello.html"];
            Assert.Contains("<pre><code class=\"language-sable\">@main\nfn main() {\n    print(&quot;Hello, world&quot;)\n}\n</code></pre>", hello, StringComparison.Ordinal);
            Assert.Contains("<a href=\"memory.html\">memory model</a>", hello, StringComparison.Ordinal);
            Assert.Contains("<blockquote>\n<p>Write the library", hello, StringComparison.Ordinal);

            var building = interpreted["building.html"];
            Assert.Contains("<h2 id=\"building\">Building</h2>", building, StringComparison.Ordinal);
            Assert.Contains("<h2 id=\"building-2\">Building</h2>", building, StringComparison.Ordinal);
            Assert.Contains("<img src=\"pipeline.png\" alt=\"The pipeline\">", building, StringComparison.Ordinal);
            Assert.DoesNotContain("<h1 id=", building, StringComparison.Ordinal);

            var index = interpreted["index.html"];
            Assert.True(
                index.IndexOf("building.html", StringComparison.Ordinal) < index.IndexOf("memory.html", StringComparison.Ordinal)
                && index.IndexOf("hello.html", StringComparison.Ordinal) < index.IndexOf("about.html", StringComparison.Ordinal),
                "the index lists the newest page first and the undated one last"
            );
            Assert.Contains("<h2 id=\"intro\">intro (2)</h2>", interpreted["tags.html"], StringComparison.Ordinal);
        } finally {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void AMissingSiteIsReported() {
        var missing = Path.Combine(Path.GetTempPath(), $"sable-ssg-missing-{Guid.NewGuid():N}");
        var source = File.ReadAllText(Path.Combine(Root, "examples/ssg/ssg.sb"));
        Assert.Equal("usage: ssg <site> <output>\n", Programs.InterpretWithArguments(source, []).Errors);
        Assert.Equal(
            $"ssg: no such file or directory: {Path.Combine(missing, "site.txt")}\n",
            Programs.InterpretWithArguments(source, [missing, missing + "-out"]).Errors
        );
    }

    static void CopyDirectory(string from, string to) {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(from)) {
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }

    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }
}
