namespace Sable.CodeGen.Tests;

/// <summary>
///     Loops over arrays that LLVM's loop vectoriser should vectorise, checked
///     by its own remarks; and the loop versioning that lets it, held to the
///     same output and the same traps as the unoptimised program.
/// </summary>
public sealed class VectorizationTests {
    /// <summary>What LLVM's loop vectoriser made of the program's loops, optimised as a build would be.</summary>
    static IReadOnlyList<VectorizationReport.Loop> Report(string source) {
        var directory = Directory.CreateTempSubdirectory("sable-vectorize-");
        try {
            var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(source, "test.sb"))], directory.FullName, checkExclusivity: false, optimised: true);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var remarks = NativeBuild.Remarks(result.LlvmIr!, NativeOptions.Of(optimise: true, debug: false), VectorizationReport.Flags);
            Assert.True(remarks.Succeeded, remarks.Output);
            return VectorizationReport.Parse(remarks.Output);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>Whether a loop on the line was vectorised (a versioned loop also leaves its checked copy, which is not).</summary>
    static bool Vectorized(IReadOnlyList<VectorizationReport.Loop> loops, int line) =>
        loops.Any(l => l.Line == line && l.Vectorized);

    const string Loops = """
                         fn copy(_ a: Int[:], _ c: inout Int[>..]) {
                             var i = 0
                             while i < a.count {
                                 c[i] = a[i]
                                 i = i + 1
                             }
                         }

                         fn addFloats(_ a: F64[:], _ b: F64[:], _ c: inout F64[>..]) {
                             var i = 0
                             while i < a.count {
                                 c[i] = a[i] * b[i] + 1.0
                                 i = i + 1
                             }
                         }

                         fn strided(_ a: Int[:], _ c: inout Int[>..]) {
                             var i = 0
                             while i < c.count {
                                 c[i] = a[i]
                                 i = i + 1
                             }
                         }

                         fn largest(_ a: Int[:]) -> Int {
                             var best = a[0]
                             for x in a {
                                 if x > best {
                                     best = x
                                 }
                             }
                             return best
                         }
                         """;

    const string Body = """
                            var a: Int[>..] = []
                            var f: F64[>..] = []
                            var i = 0
                            while i < 100 {
                                a.append(i * 7 % 31)
                                f.append(1.5)
                                i = i + 1
                            }
                            var c = a
                            c[0] = 99
                            copy(a, &c)
                            var g = f
                            addFloats(f, f, &g)
                            var d: Int[>..] = [0, 0, 0]
                            strided(a, &d)
                            print("\(c[0]) \(c[99]) \(g[5]) \(d[2]) \(largest(a))")
                        """;

    [Fact]
    public void LoopsOverArraysVectorize() {
        if (!Programs.CanBuild) {
            return;
        }

        var loops = Report(Programs.Program(Loops, Body));
        // Lines: the module line, then the declarations from line 2.
        Assert.True(Vectorized(loops, 4) || Vectorized(loops, 5), VectorizationReport.Format(loops)); // copy
        Assert.True(Vectorized(loops, 12) || Vectorized(loops, 13), VectorizationReport.Format(loops)); // floats, element-wise
        Assert.True(Vectorized(loops, 20) || Vectorized(loops, 21), VectorizationReport.Format(loops)); // copy, bounded by the destination
        Assert.True(loops.Any(l => l.Line is >= 26 and <= 30 && l.Vectorized), VectorizationReport.Format(loops)); // max
    }

    [Fact]
    public void VersionedLoopsAgreeWithTheInterpreter() =>
        Assert.Equal("0 11 3.25 14 30\n", Programs.AssertAgree(Programs.Program(Loops, Body)).Stdout);

    /// <summary>
    ///     A loop that indexes past an array's end runs as written: what it did
    ///     before the bad index, then the trap, at the access.
    /// </summary>
    [Fact]
    public void AnIndexOutOfRangeStillTrapsWhereItWas() {
        var execution = Programs.AssertAgree(
            Programs.Program(
                """
                fn fill(_ a: Int[:], _ c: inout Int[>..]) {
                    var i = 0
                    while i < a.count {
                        print(i)
                        c[i] = a[i]
                        i = i + 1
                    }
                }
                """,
                """
                    var c: Int[>..] = [0, 0]
                    fill([1, 2, 3, 4], &c)
                    print(c[0])
                """
            )
        );
        Assert.Equal("0\n1\n2\n", execution.Stdout);
        Assert.Contains("array index out of range", execution.Stderr, StringComparison.Ordinal);
        Assert.Contains("test.sb:6:9", execution.Stderr, StringComparison.Ordinal);
    }

    /// <summary>A loop that starts below zero reads index -1 first, and traps there.</summary>
    [Fact]
    public void ANegativeStartStillTraps() {
        var execution = Programs.AssertAgree(
            Programs.Program(
                """
                fn sumFrom(_ a: Int[:], _ start: Int) -> Int {
                    var total = 0
                    var i = start
                    while i < a.count {
                        total = total + a[i]
                        i = i + 1
                    }
                    return total
                }
                """,
                """
                    print(sumFrom([1, 2, 3], 1))
                    print(sumFrom([1, 2, 3], -1))
                """
            )
        );
        Assert.Equal("5\n", execution.Stdout);
        Assert.Contains("array index out of range", execution.Stderr, StringComparison.Ordinal);
    }

    /// <summary>What a loop works out leaves either version of it: a running total, the last index.</summary>
    [Fact]
    public void ALoopsResultsLeaveEitherVersion() =>
        Assert.Equal(
            "10 4\n6 3\n",
            Programs.AssertAgree(
                Programs.Program(
                    """
                    fn scan(_ a: Int[:], _ b: Int[:]) {
                        var total = 0
                        var i = 0
                        while i < a.count {
                            total = total + a[i] * b[i]
                            i = i + 1
                        }
                        print("\(total) \(i)")
                    }
                    """,
                    """
                        let short: Int[>..] = [1, 1, 1]
                        scan([1, 2, 3, 4], [1, 1, 1, 1])
                        scan([1, 2, 3], short)
                    """
                )
            ).Stdout
        );

    /// <summary><c>sable build --vectorize-report</c> names each loop by its place, and says why one was not vectorised.</summary>
    [Fact]
    public void TheReportNamesEachLoopAndWhyNot() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "loops.sb"), Programs.Program(Loops, Body));
        var report = SableProcess.Run(fixture.Root, fixture.Home, "build", "loops.sb", "--vectorize-report");
        Assert.Equal(0, report.ExitCode);
        Assert.Matches(@"loops\.sb:\d+:\d+: vectorized \(vectorization width: \d+", report.Output);
        Assert.Matches(@"loops\.sb:\d+:\d+: not vectorized\n    \S", report.Output);
        Assert.Matches(@"\d+ of \d+ loops vectorized\n$", report.Output);
        Assert.False(File.Exists(Path.Combine(fixture.Root, Platform.Executable("loops"))));
    }

    /// <summary>Remarks become loops: the reasons before a loop's verdict are its own, and a loop inlined twice is reported once.</summary>
    [Fact]
    public void RemarksAreGroupedByLoop() {
        var loops = VectorizationReport.Parse(
            """
            remark: a.sb:3:5: loop not vectorized: call instruction cannot be vectorized [-Rpass-analysis=loop-vectorize]
            remark: a.sb:3:5: loop not vectorized [-Rpass-missed=loop-vectorize]
            a.sb:9:1: remark: vectorized loop (vectorization width: 4, interleaved count: 2) [-Rpass=loop-vectorize]
            remark: a.sb:3:5: loop not vectorized: call instruction cannot be vectorized [-Rpass-analysis=loop-vectorize]
            remark: a.sb:3:5: loop not vectorized [-Rpass-missed=loop-vectorize]
            """
        );
        Assert.Equal(2, loops.Count);
        Assert.Equal(["call instruction cannot be vectorized"], loops[0].Reasons.Select(static r => r.Text));
        Assert.True(loops[1].Vectorized);
        Assert.Equal("vectorization width: 4, interleaved count: 2", loops[1].How);
        Assert.Equal(
            "a.sb:3:5: not vectorized\n    call instruction cannot be vectorized\na.sb:9:1: vectorized (vectorization width: 4, interleaved count: 2)\n1 of 2 loops vectorized\n",
            VectorizationReport.Format(loops)
        );
    }
}
