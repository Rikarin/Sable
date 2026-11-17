namespace Sable.Symbols.Tests;

/// <summary>Definite return and unreachable code, around loops and their labels.</summary>
public sealed class ReachabilityTests {
    static IReadOnlyList<Diagnostic> DiagnosticsIn(string source) => [
        ..Compilation.Create($"module M\n{source}").GetAllDiagnostics()
    ];

    static bool Has(string source, ErrorCode code) => DiagnosticsIn(source).Any(d => d.Code == code);

    [Fact]
    public void ALabelledBreakFromAnInnerLoopEndsTheOuterOne() =>
        // 'break outer' leaves the outer 'while true', so control can fall off
        // the end of an '-> Int' body.
        Assert.True(Has(
            """
            fn f() -> Int {
                outer: while true {
                    while true { break outer }
                }
            }
            """,
            ErrorCode.ERR_NotAllPathsReturn
        ));

    [Fact]
    public void WhatFollowsALoopLeftByALabelledBreakIsReachable() =>
        Assert.False(Has(
            """
            fn f(_ x: Int) -> Int {
                outer: while true {
                    while true { break outer }
                }
                return x
            }
            """,
            ErrorCode.WRN_UnreachableCode
        ));

    [Fact]
    public void AnUnlabelledBreakInAnInnerLoopStaysThere() =>
        Assert.False(Has(
            """
            fn f() -> Int {
                while true {
                    while true { break }
                    return 1
                }
            }
            """,
            ErrorCode.ERR_NotAllPathsReturn
        ));

    [Fact]
    public void ABreakToAnotherLabelDoesNotEndThisLoop() =>
        Assert.False(Has(
            """
            fn f() -> Int {
                inner: while true {
                    while true { break inner }
                    return 1
                }
                return 2
            }
            """,
            ErrorCode.ERR_NotAllPathsReturn
        ));

    [Fact]
    public void ARepeatWhoseBodyBreaksDoesNotAlwaysExit() =>
        Assert.True(Has(
            """
            fn f() -> Int {
                repeat { break } while true
            }
            """,
            ErrorCode.ERR_NotAllPathsReturn
        ));
}
