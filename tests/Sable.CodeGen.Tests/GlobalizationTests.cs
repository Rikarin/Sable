using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Locales (Sable.Globalization, and the runtime's user_locale), natively and interpreted. The
///     library's own tests (tests/lib/Sable.Globalization) say more.
/// </summary>
[Native]
public sealed class GlobalizationTests {
    [Fact]
    public void ALocaleIsTheSameNativelyAndInterpreted() =>
        Assert.Equal(
            "1.234.567,89\n١٬٢٣٤٫٥\n12,34,567\n-$5.50\nmardi 17 novembre 2026\nFew\na, b und c\nGreater\ntrue\n0.5\n1234.5\n",
            AssertAgree("""
                module P
                import Sable.Globalization
                import Sable.Time

                @main
                fn main() {
                    print(1234567.89.format(.decimal, locale: Locale.of("de")!))
                    print(1234.5.format(.decimal, locale: Locale.of("ar-EG")!))
                    print(1234567.format(.decimal, locale: Locale.of("hi")!))
                    print((-5.5).format(.currency("USD"), locale: Locale.of("en")!))
                    print(Date.of(year: 2026, month: 11, day: 17)!.format(.Full, locale: Locale.of("fr")!))
                    print(PluralRules(Locale.of("cs")!).category(3))
                    print(ListFormatter(Locale.of("de")!).format(["a", "b", "c"]))
                    let swedish = Collator(Locale.of("sv")!)
                    print(swedish.compare("ärm", "zon"))
                    print(swedish.sortKey("ärm") > swedish.sortKey("zon"))
                    print(try! F64.parse("50 %", format: .percent, locale: Locale.of("de")!))
                    print(1234.5.format(.decimal, locale: .invariant))
                }
                """).Stdout
        );

    [Fact]
    public void TheSystemsLocaleIsTheSameNativelyAndInterpreted() =>
        AssertAgree("""
            module P
            import Sable.Globalization

            @main
            fn main() {
                print(Runtime.systemText(5))
                print(Locale.current.tag)
            }
            """);
}
