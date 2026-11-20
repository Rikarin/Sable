namespace Sable.CodeGen.Tests;

/// <summary>Which tests build natively as well as interpreting (<see cref="NativeMode" />): those marked, and every one under <c>SABLE_TEST_NATIVE=all</c>.</summary>
public sealed class NativeModeTests {
    [Fact]
    public void AnUnmarkedTestBuildsNativelyOnlyWhenAllDo() => Assert.Equal(NativeMode.All, NativeMode.Wanted);

    [Fact]
    [Native]
    public void AMarkedTestBuildsNatively() => Assert.True(NativeMode.Wanted);

    [Fact]
    public void ATestOfAMarkedClassBuildsNatively() => Assert.True(MarkedClass.Wanted());

    [Fact]
    public void ALambdaInAMarkedTestStillCounts() => Assert.True(MarkedClass.WantedInLambda());

    [Native]
    static class MarkedClass {
        public static bool Wanted() => NativeMode.Wanted;

        public static bool WantedInLambda() => new Func<bool>(() => NativeMode.Wanted)();
    }
}
