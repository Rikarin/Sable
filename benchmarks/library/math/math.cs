// Port of math.sb.
[Benchmark("library/math")]
static class LibraryMath {
    public static string Run() {
        checked {
            var total = 0.0;
            for (var i = 1; i < 500001; i++) {
                var x = i / 1000.0;
                total += Math.Sin(x) + Math.Cos(x) + Math.Log(x) + Math.Exp(-x) + Math.Atan2(x, 3.0) + double.Hypot(x, 2.0);
            }

            return total.ToString("F4");
        }
    }
}
