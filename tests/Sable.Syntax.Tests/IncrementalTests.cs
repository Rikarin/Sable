using System.Diagnostics;
using System.Text;

namespace Sable.Syntax.Tests;

/// <summary>
///     Incremental reparse. The contract is simple and absolute: applying an edit
///     must give the tree a full parse of the resulting text would have given.
///     Every test here checks that, so however clever the reuse gets, it cannot
///     quietly produce a different tree.
/// </summary>
public sealed class IncrementalTests {
    static void AssertSameAsFullParse(SyntaxTree before, TextChange change) {
        var expectedText = before.Text.WithChange(change);

        var incremental = before.WithChange(change);
        var full = SyntaxTree.Parse(expectedText, before.Options);

        Assert.Equal(expectedText.Text, incremental.ToFullString());

        Assert.True(
            TreeDump.Dump(full.Root) == TreeDump.Dump(incremental.Root),
            $"""
             incremental tree differs from a full parse
               edit: {change}
               ---- full ----
               {TreeDump.Dump(full.Root)}
               ---- incremental ----
               {TreeDump.Dump(incremental.Root)}
             """
        );

        Assert.Equal(
            full.Diagnostics.Select(static d => $"{d.Id}@{d.Span}").ToList(),
            incremental.Diagnostics.Select(static d => $"{d.Id}@{d.Span}").ToList()
        );
    }

    const string Sample = """
                          module App

                          import Sable.IO

                          public struct Point {
                              public var x: F64
                              public var y: F64

                              public fn length() -> F64 => (x * x + y * y).sqrt()
                          }

                          public union Shape {
                              Circle(radius: F64)
                              Rect(width: F64, height: F64)
                          }

                          public enum Direction { North, South, East, West }

                          public fn area(_ s: Shape) -> F64 {
                              return match s {
                                  case .Circle(let r) => 3.14 * r * r
                                  case .Rect(let w, let h) => w * h
                              }
                          }

                          public fn main() {
                              let p = Point { x: 1.0, y: 2.0 }
                              for i in 0..<10 {
                                  print("\(i): \(p.length())")
                              }
                          }
                          """;

    static SyntaxTree ParseSample() => SyntaxTree.Parse(new(Sample, "sample.sb"));

    // ---- Targeted edits ----------------------------------------------------

    [Fact]
    public void InsertingInsideAFunctionBody() {
        var tree = ParseSample();
        var index = Sample.IndexOf("3.14", StringComparison.Ordinal);
        AssertSameAsFullParse(tree, TextChange.Replace(new(index, 4), "3.14159"));
    }

    [Fact]
    public void InsertingANewMemberInTheMiddle() {
        var tree = ParseSample();
        var index = Sample.IndexOf("    public fn length()", StringComparison.Ordinal);
        AssertSameAsFullParse(
            tree,
            TextChange.Insert(index, "    public var z: F64\n\n")
        );
    }

    [Fact]
    public void DeletingAWholeMember() {
        var tree = ParseSample();
        var start = Sample.IndexOf("public enum Direction", StringComparison.Ordinal);
        var end = Sample.IndexOf('\n', start) + 1;
        AssertSameAsFullParse(tree, TextChange.Delete(TextSpan.FromBounds(start, end)));
    }

    [Fact]
    public void RenamingATypeUsedLater() {
        var tree = ParseSample();
        var index = Sample.IndexOf("Point {", StringComparison.Ordinal);
        AssertSameAsFullParse(tree, TextChange.Replace(new(index, 5), "Vector"));
    }

    [Fact]
    public void EditAtTheVeryStart() {
        var tree = ParseSample();
        AssertSameAsFullParse(tree, TextChange.Insert(0, "// a leading comment\n"));
    }

    [Fact]
    public void EditAtTheVeryEnd() {
        var tree = ParseSample();
        AssertSameAsFullParse(tree, TextChange.Insert(Sample.Length, "\nfn extra() { }\n"));
    }

    [Fact]
    public void DeletingEverything() {
        var tree = ParseSample();
        AssertSameAsFullParse(tree, TextChange.Delete(new(0, Sample.Length)));
    }

    [Fact]
    public void AnEditThatBreaksTheFile() {
        var tree = ParseSample();
        var index = Sample.IndexOf("public union Shape {", StringComparison.Ordinal);
        AssertSameAsFullParse(tree, TextChange.Insert(index + 19, "   // oops"));
    }

    [Fact]
    public void AnEditThatOpensAComment() {
        // Typing the second '/' must not let the old tokens after it survive.
        var tree = ParseSample();
        var index = Sample.IndexOf("public enum", StringComparison.Ordinal);
        AssertSameAsFullParse(tree, TextChange.Insert(index, "/*"));
    }

    [Fact]
    public void AnEditThatOpensAString() {
        var tree = ParseSample();
        var index = Sample.IndexOf("public enum", StringComparison.Ordinal);
        AssertSameAsFullParse(tree, TextChange.Insert(index, "\""));
    }

    [Fact]
    public void AnEditThatRemovesABrace() {
        var tree = ParseSample();
        var index = Sample.IndexOf("public struct Point {", StringComparison.Ordinal) + 20;
        AssertSameAsFullParse(tree, TextChange.Delete(new(index, 1)));
    }

    [Fact]
    public void AnEditInsideAUnionBody() {
        var tree = ParseSample();
        var index = Sample.IndexOf("Rect(width", StringComparison.Ordinal);
        AssertSameAsFullParse(tree, TextChange.Replace(new(index, 4), "Square"));
    }

    [Fact]
    public void AnEditInsideAnEnumBody() {
        var tree = ParseSample();
        var index = Sample.IndexOf("South", StringComparison.Ordinal);
        AssertSameAsFullParse(tree, TextChange.Replace(new(index, 5), "Down"));
    }

    [Fact]
    public void SequentialEditsStayCorrect() {
        var tree = ParseSample();
        var text = Sample;

        var changes = new List<TextChange>();
        for (var i = 0; i < 20; i++) {
            var index = text.IndexOf("F64", StringComparison.Ordinal);
            if (index < 0) {
                break;
            }

            var change = TextChange.Replace(new(index, 3), "Double");
            changes.Add(change);
            text = text[..index] + "Double" + text[(index + 3)..];
        }

        foreach (var change in changes) {
            AssertSameAsFullParse(tree, change);
            tree = tree.WithChange(change);
        }

        Assert.Equal(text, tree.ToFullString());
    }

    // ---- Typing simulation -------------------------------------------------

    [Fact]
    public void TypingAFunctionOneCharacterAtATime() {
        var tree = SyntaxTree.Parse(new(Sample, "sample.sb"));
        const string typed = "\npublic fn typed(a: Int) -> Int => a * 2\n";

        var position = Sample.Length;
        foreach (var c in typed) {
            var change = TextChange.Insert(position, c.ToString());
            AssertSameAsFullParse(tree, change);
            tree = tree.WithChange(change);
            position++;
        }
    }

    [Fact]
    public void DeletingBackwardsOneCharacterAtATime() {
        var tree = ParseSample();
        var remaining = Sample.Length;

        for (var i = 0; i < 60 && remaining > 0; i++) {
            var change = TextChange.Delete(new(remaining - 1, 1));
            AssertSameAsFullParse(tree, change);
            tree = tree.WithChange(change);
            remaining--;
        }
    }

    // ---- Random edits over the real corpus ---------------------------------

    [Fact]
    public void RandomEditsOnTheCorpusMatchAFullParse() {
        var random = new Random(20261120);
        var directory = Path.Combine(TestHelpers.RepositoryRoot, "examples");

        string[] insertions = [
            " ", "\n", "x", "}", "{", "(", ")", "\"", "/*", "//", "fn ", "let ",
            ".", ",", "#if A\n", "public ", "0", "=>", "<", ">"
        ];

        foreach (var file in Directory.GetFiles(directory, "*.sb")) {
            var original = File.ReadAllText(file);

            for (var iteration = 0; iteration < 120; iteration++) {
                var tree = SyntaxTree.Parse(new(original, file));

                var position = random.Next(original.Length + 1);
                var change = random.Next(3) switch {
                    0 => TextChange.Insert(position, insertions[random.Next(insertions.Length)]),
                    1 => TextChange.Delete(new(position, Math.Min(random.Next(1, 12), original.Length - position))),
                    _ => TextChange.Replace(
                        new(position, Math.Min(random.Next(0, 6), original.Length - position)),
                        insertions[random.Next(insertions.Length)]
                    ),
                };

                AssertSameAsFullParse(tree, change);
            }
        }
    }

    [Fact]
    public void RandomEditsOnAGeneratedFileMatchAFullParse() {
        var builder = new StringBuilder();
        builder.AppendLine("module Generated");
        for (var i = 0; i < 40; i++) {
            builder.AppendLine(
                $$"""
                  public struct Item{{i}} {
                      public var id: U64
                      public fn describe() -> String => "item"
                      public fn total(_ qty: Int) -> Int {
                          var sum = 0
                          for k in 0..<qty { sum += k }
                          return sum
                      }
                  }
                  """
            );
        }

        var original = builder.ToString();
        var random = new Random(20261121);

        for (var iteration = 0; iteration < 200; iteration++) {
            var tree = SyntaxTree.Parse(new(original, "generated.sb"));
            var position = random.Next(original.Length);

            var change = random.Next(2) == 0
                ? TextChange.Insert(position, "qz")
                : TextChange.Delete(new(position, Math.Min(3, original.Length - position)));

            AssertSameAsFullParse(tree, change);
        }
    }

    // ---- Reuse actually happens, and is fast -------------------------------

    internal static SourceText LargeSource() {
        var builder = new StringBuilder();
        builder.AppendLine("module Bench");
        for (var i = 0; i < 1000; i++) {
            builder.AppendLine(
                $$"""
                  public struct Item{{i}} {
                      public var id: U64
                      public var name: String
                      public fn describe() -> String => "item"
                      public fn total(_ qty: Int) -> Int {
                          var sum = 0
                          for k in 0..<qty { sum += k * {{i}} }
                          return sum
                      }
                  }
                  """
            );
        }

        return new(builder.ToString(), "bench.sb");
    }

    [Fact]
    public void MostMembersAreReusedAfterASmallEdit() {
        var source = LargeSource();
        var tree = SyntaxTree.Parse(source);
        var position = source.Text.IndexOf("Item500", StringComparison.Ordinal) + 4;

        var updated = tree.WithChange(TextChange.Insert(position, "z"));

        var before = ((CompilationUnitSyntax)tree.Root).Members;
        var after = ((CompilationUnitSyntax)updated.Root).Members;
        Assert.Equal(before.Count, after.Count);

        // Reused members are the very same green node, so the red facades
        // wrap identical underlying objects.
        var shared = 0;
        for (var i = 0; i < before.Count; i++) {
            if (ReferenceEquals(before[i].GreenIdentity, after[i].GreenIdentity)) {
                shared++;
            }
        }

        Assert.True(
            shared > before.Count * 0.9,
            $"only {shared} of {before.Count} members were reused"
        );
    }

    [Fact]
    public void ReuseIsSkippedForFilesWithDirectives() {
        const string source = """
                              module D
                              #if DEBUG
                              fn a() { }
                              #endif
                              fn b() { }
                              """;

        var tree = SyntaxTree.Parse(new(source, "d.sb"));
        AssertSameAsFullParse(tree, TextChange.Insert(source.Length, "\nfn c() { }\n"));
    }

    /// <summary>
    ///     An edit past a directive: the lexer that takes over after the
    ///     reused tokens has to know which branches are open there, or the
    ///     rest of an <c>#else</c> lexes as if no <c>#if</c> were open, and a
    ///     branch that is off as if it were on. Found by the coverage-guided
    ///     fuzzer (tools/fuzz/coverage.sh).
    /// </summary>
    [Theory]
    [InlineData("module D\n#if NOPE\nfn a() { }\n#else\nfn b() { }\nfn c() { }\n#endif\n", "fn c", "fn d")]
    [InlineData("module D\n#if NOPE\nlet g = \"\n#else\npublic fn e(_ m: String)\n#endif\n", "public fn e", "")]
    [InlineData("module D\n#if YES\nfn a() { }\n#else\nfn b() { }\nfn c() { }\n#endif\n", "fn c", "fn d")]
    public void AnEditPastADirectiveMatchesAFullParse(string source, string replaced, string replacement) {
        var tree = SyntaxTree.Parse(new SourceText(source, "d.sb"), ParseOptions.Default.WithSymbols("YES"));
        AssertSameAsFullParse(tree, TextChange.Replace(new(source.IndexOf(replaced, StringComparison.Ordinal), replaced.Length), replacement));
    }

    // ---- Chained edits ------------------------------------------------------

    /// <summary>
    ///     Every token with its full text and both flags, then every diagnostic:
    ///     stricter than <see cref="TreeDump" />, which drops punctuation and
    ///     trivia, because the bugs that survive it live exactly there.
    /// </summary>
    static string StrictDump(SyntaxTree tree) {
        var builder = new StringBuilder();

        void Walk(SyntaxNodeOrToken item, int depth) {
            if (item.IsToken) {
                var token = item.AsToken();
                builder.Append(' ', depth).Append(token.Kind).Append(" [").Append(token.ToFullString()).Append(']')
                    .Append(token.IsMissing ? " missing" : "")
                    .Append(token.TerminatesStatement ? " ends" : "")
                    .Append('\n');
                return;
            }

            var node = item.AsNode()!;
            builder.Append(' ', depth).Append(node.Kind).Append('\n');
            foreach (var child in node.ChildNodesAndTokens()) {
                Walk(child, depth + 1);
            }
        }

        Walk(tree.Root, 0);
        foreach (var diagnostic in tree.Diagnostics) {
            builder.Append(diagnostic.Id).Append('@').Append(diagnostic.Span).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Edits applied one after another to the same tree — each from the
    ///     previous incremental result, never from a fresh parse — including
    ///     edits to files that already have errors. Garbage accumulates, which
    ///     is what an editor's buffer actually looks like.
    /// </summary>
    [Fact]
    public void ChainedEditsMatchAFullParseExactly() {
        string[] snippets = [
            "", " ", "\n", "x", "(", ")", "{", "}", "[", "]", ".", ",", ";", "fn ", "let ", "struct ",
            "+", "=", "\"", "\\(", "<", ">", "?", "//", "/*", "*/", "case ", "=>", "1", "_", "while ", "else ",
        ];

        var files = Directory.GetFiles(Path.Combine(TestHelpers.RepositoryRoot, "tests", "corpus"), "*.sb")
            .Concat(Directory.GetFiles(Path.Combine(TestHelpers.RepositoryRoot, "examples"), "*.sb"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var random = new Random(20261121);
        for (var round = 0; round < 60; round++) {
            var tree = SyntaxTree.Parse(File.ReadAllText(files[random.Next(files.Length)]));
            var history = new List<string>();

            for (var step = 0; step < 25; step++) {
                var length = tree.Text.Length;
                var start = random.Next(length + 1);
                var deleted = random.Next(4) == 0 ? Math.Min(random.Next(6), length - start) : 0;
                var inserted = snippets[random.Next(snippets.Length)];
                history.Add($"@{start}-{deleted}+{inserted}");

                tree = tree.WithChange(TextChange.Replace(new TextSpan(start, deleted), inserted));
                var full = SyntaxTree.Parse(tree.Text.Text);

                var expected = StrictDump(full);
                var actual = StrictDump(tree);
                Assert.True(
                    expected == actual,
                    $"round {round}, step {step}: incremental differs from a full parse after {string.Join(" ", history)}"
                );
            }
        }
    }

    [Fact]
    public void AnErrorReportedAtTheNextMemberSurvivesAnEditElsewhere() {
        // 'let a = 1 +' is reported at 'fn', the next member's first token;
        // the member that caused it must not be reused without it.
        var tree = SyntaxTree.Parse("module M\nlet a = 1 +\nfn b() { }\nfn c() { }\n");
        Assert.NotEmpty(tree.Diagnostics);

        var edited = tree.WithChange(TextChange.Insert(tree.Text.Length - 2, " "));
        Assert.Equal(StrictDump(SyntaxTree.Parse(edited.Text.Text)), StrictDump(edited));
    }

    [Fact]
    public void ABracketOpenedEarlierChangesWhereLaterStatementsEnd() {
        // Inside an unbalanced '(' newlines do not end statements, so opening
        // one at the top changes the flags of every token after it.
        var tree = SyntaxTree.Parse("module M\nfn f() {\n    let a = 1\n    let b = 2\n}\nfn g() { }\n");
        var edited = tree.WithChange(TextChange.Insert(tree.Text.Text.IndexOf("let a", StringComparison.Ordinal), "("));
        Assert.Equal(StrictDump(SyntaxTree.Parse(edited.Text.Text)), StrictDump(edited));

        var closed = edited.WithChange(TextChange.Delete(new TextSpan(edited.Text.Text.IndexOf('('), 1)));
        Assert.Equal(StrictDump(SyntaxTree.Parse(closed.Text.Text)), StrictDump(closed));
    }

    /// <summary>
    ///     Whether <c>e&lt;r</c> names a record's type is decided by looking for
    ///     its <c>&gt;</c>, and that lookahead runs past the function's closing
    ///     brace into the next member. An edit there, which leaves the function's
    ///     own tokens alone, changes how the function parses, so the function
    ///     must not be reused. Found by the coverage-guided parser fuzzer
    ///     (crash-a7c378c6a01b92bd0f17bae80eca2c4d393d98bf), minimised.
    /// </summary>
    [Theory]
    [InlineData("struct{//\npublic fn s(e:B){()&e<r}public n e<>{}")]
    [InlineData("module M\nstruct S {\n    fn f(e: B) { () & e<r }\n    public n e<>{}\n}\n")]
    public void LookaheadPastAMembersEndIsReparsedWhenItChanges(string source) {
        var tree = SyntaxTree.Parse(source);
        var at = source.LastIndexOf("<>", StringComparison.Ordinal);

        // The '>' now closes 'e<r', and the brace after it is a record's.
        var deleted = tree.WithChange(TextChange.Delete(new TextSpan(at, 1)));
        Assert.Equal(StrictDump(SyntaxTree.Parse(deleted.Text.Text)), StrictDump(deleted));
        AssertSameAsFullParse(tree, TextChange.Delete(new TextSpan(at, 1)));

        // And back: the old reading of 'e<r' must not be reused either.
        var restored = deleted.WithChange(TextChange.Insert(at, "<"));
        Assert.Equal(StrictDump(SyntaxTree.Parse(restored.Text.Text)), StrictDump(restored));
    }

    /// <summary>
    ///     Attributes at the top of a file are read before the members are, and
    ///     handed to the first; when no declaration follows them, the member
    ///     list skips the next token without a word. The same attributes after
    ///     another member are read by a member of their own, and the token after
    ///     them is reported. So the member after that token was parsed from
    ///     state carried over, not just from its tokens, and must not be reused
    ///     once something comes before the attributes. Found by the
    ///     coverage-guided parser fuzzer
    ///     (crash-1eff0e9a860a8f54f892b466f1e9475f942952b5), minimised.
    /// </summary>
    [Theory]
    [InlineData("// other programs d=> v \n     @main\n ne    UInt:on F32: C�", 0, 18, "\0 }\nexA ")]
    [InlineData("@main\nne\nx: Int\ny: Int\n", 0, 0, "fn f() {}\n")]
    public void AMemberAfterSilentlySkippedTextIsReparsed(string source, int start, int length, string replacement) {
        var tree = SyntaxTree.Parse(source);
        var change = new TextChange(new TextSpan(start, length), replacement);
        AssertSameAsFullParse(tree, change);

        var edited = tree.WithChange(change);
        Assert.Equal(StrictDump(SyntaxTree.Parse(edited.Text.Text)), StrictDump(edited));

        // And back.
        var undone = edited.WithChange(new TextChange(new TextSpan(start, replacement.Length), source.Substring(start, length)));
        Assert.Equal(StrictDump(SyntaxTree.Parse(source)), StrictDump(undone));
    }

    /// <summary>
    ///     A raw string opens with any number of <c>#</c>s, so text typed after
    ///     a run of them can turn the whole run into one token. Lexing again
    ///     from two tokens before the edit, as for any other edit, left the
    ///     run's first <c>#</c>s standing alone. Found by the coverage-guided
    ///     parser fuzzer (crash-87b0b5ea4e1987617584a032e1607d71c3fd0ca8),
    ///     minimised.
    /// </summary>
    [Theory]
    [InlineData("######", 4, 2, "################\"#####.0}")]
    [InlineData("module Sable.Mat\"s\"r and flo\uFFFD\uFFFD################", 35, 11, "################\"#####.0}\n")]
    [InlineData("module M\nlet a = 1\n##########\n", 26, 0, "\"x\"##########")]
    public void TypingARawStringsQuoteAfterARunOfHashesRelexesTheRun(string source, int start, int length, string replacement) {
        var tree = SyntaxTree.Parse(source);
        var change = new TextChange(new TextSpan(start, length), replacement);
        AssertSameAsFullParse(tree, change);
    }

    /// <summary>
    ///     The nesting limit is reported once per file, so whether a member
    ///     too deep says so depends on every member before it. One that reached
    ///     the limit is never reused.
    /// </summary>
    [Fact]
    public void AMemberPastTheNestingLimitIsReparsed() {
        static string Deep(string name) => $"let {name} = {new string('(', 300)}1{new string(')', 300)}\n";

        var first = Deep("a");
        var source = first + "let m = 1\n" + Deep("b") + "let c = 1\n";
        var tree = SyntaxTree.Parse(source);

        // 'a' no longer goes too deep, so 'b' is now the one to say so.
        var change = new TextChange(new TextSpan(0, first.Length), "let a = 1\n");
        AssertSameAsFullParse(tree, change);
    }
}

/// <summary>
///     How fast an incremental edit is. Timing is measured apart from the
///     other tests (the collection runs alone), and against a full parse of
///     the same file on the same machine, so that a slow or loaded machine
///     makes both slower alike and only a real regression fails it.
/// </summary>
[Collection(TimingCollection.Name)]
[Trait("Category", "Timing")]
public sealed class IncrementalTimingTests {
    [Fact]
    public void AOneCharacterEditInATenThousandLineFileIsFast() {
        var source = IncrementalTests.LargeSource();
        Assert.True(source.LineCount > 10_000, $"expected a large file, got {source.LineCount}");

        var tree = SyntaxTree.Parse(source);
        var position = source.Text.IndexOf("Item500", StringComparison.Ordinal) + 4;

        // Warm up both paths, so that what is measured is the JIT's
        // optimised code. A method is recompiled with optimisations after
        // thirty calls, on a background thread, once no new method has been
        // compiled for a while (tiered compilation) — and in a test host,
        // with the rest of the runner compiling, that can take a second or
        // two. So both run, pausing for the JIT, for two seconds first. (The
        // project turns dynamic PGO off, for the same reason.)
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 2) {
            for (var i = 0; i < 10; i++) {
                tree.WithChange(TextChange.Insert(position, "z"));
            }

            SyntaxTree.Parse(source);
            Thread.Sleep(100);
        }

        // Edits and full parses, interleaved so that both see the same
        // load, each timed alone and the best of each kept: what the code
        // costs, not what the machine was doing besides. (An edit allocates
        // its token array on the large-object heap; a collection the parses'
        // garbage brings on lands in whichever edit comes next, which on a
        // loaded runner made a batch's average ten times the edit's cost.)
        var perEdit = double.MaxValue;
        var perParse = double.MaxValue;
        for (var round = 0; round < 10; round++) {
            for (var i = 0; i < 3; i++) {
                var edit = Stopwatch.StartNew();
                tree.WithChange(TextChange.Insert(position, "z"));
                perEdit = Math.Min(perEdit, edit.Elapsed.TotalMilliseconds);
            }

            var parse = Stopwatch.StartNew();
            SyntaxTree.Parse(source);
            perParse = Math.Min(perParse, parse.Elapsed.TotalMilliseconds);
        }

        // The plan's exit criterion is 2 ms, and this measures about 1 ms on
        // a development machine, against about 20 ms to reparse. A
        // regression to reparsing the file, or most of it, brings the two
        // together; a sixth of a parse is the bound, which holds on any
        // machine however slow, as both are slower alike.
        var message = $"incremental reparse took {perEdit:F2} ms per edit, a full parse {perParse:F2} ms, for {source.LineCount:N0} lines";
        Assert.True(perEdit * 6 < perParse, message);
        Console.WriteLine(message);
    }
}
