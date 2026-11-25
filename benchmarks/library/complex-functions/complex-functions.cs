using System.Numerics;

// Port of complex-functions.sb.
[Benchmark("library/complex-functions")]
static class LibraryComplexFunctions {
    public static string Run() {
        var total = Complex.Zero;
        var magnitudes = 0.0;
        for (var row = 0; row < 480; row++) {
            for (var column = 0; column < 150; column++) {
                var z = new Complex(column / 60.0 - 1.246, row / 180.0 - 1.329);
                var w = new Complex(column / 150.0 + 0.5, 0.25);
                total = total + Complex.Exp(z) + Complex.Log(z) + Complex.Sqrt(z) + Complex.Pow(z, w);
                total = total + Complex.Sin(z) + Complex.Cos(z) + Complex.Sinh(z) + Complex.Cosh(z);
                total = total + Complex.Tan(z) + Complex.Tanh(z);
                total = total + Complex.Asin(z) + Complex.Acos(z) + Complex.Atan(z);
                magnitudes += z.Magnitude + z.Phase;
                total += Complex.FromPolarCoordinates(z.Magnitude, z.Phase);
            }
        }

        return $"{total.Real:F2} {total.Imaginary:F2} {magnitudes:F2}";
    }
}
