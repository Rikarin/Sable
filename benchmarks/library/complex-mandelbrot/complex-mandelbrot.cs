using System.Numerics;

// Port of complex-mandelbrot.sb, with System.Numerics.Complex.
[Benchmark("library/complex-mandelbrot")]
static class LibraryComplexMandelbrot {
    public static string Run() {
        checked {
            long rounds = 0;
            for (var row = 0; row < 480; row++) {
                for (var column = 0; column < 640; column++) {
                    var c = new Complex(column / 256.0 - 2.0, row / 192.0 - 1.25);
                    var z = Complex.Zero;
                    var count = 0;
                    while (count < 200 && z.Real * z.Real + z.Imaginary * z.Imaginary <= 4.0) {
                        z = z * z + c;
                        count++;
                    }

                    rounds += count;
                }
            }

            var w = Complex.One;
            var total = Complex.Zero;
            for (var i = 0; i < 500000; i++) {
                w = new Complex(1.0, 0.5) / (w + new Complex(2.0, i % 16 / 16.0));
                total += w;
            }

            return $"{rounds} {total.Real:F3} {total.Imaginary:F3}";
        }
    }
}
