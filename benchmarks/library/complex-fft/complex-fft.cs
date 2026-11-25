using System.Numerics;

// Port of complex-fft.sb, with arrays of System.Numerics.Complex.
[Benchmark("library/complex-fft")]
static class LibraryComplexFft {
    static void Transform(Complex[] values, Complex[] roots) {
        checked {
            var n = values.Length;
            var j = 0;
            for (var i = 1; i < n; i++) {
                var bit = n >> 1;
                while ((j & bit) != 0) {
                    j ^= bit;
                    bit >>= 1;
                }

                j ^= bit;
                if (i < j) {
                    (values[i], values[j]) = (values[j], values[i]);
                }
            }

            for (var length = 2; length <= n; length *= 2) {
                var half = length / 2;
                var step = n / length;
                for (var start = 0; start < n; start += length) {
                    for (var k = 0; k < half; k++) {
                        var even = values[start + k];
                        var odd = values[start + k + half] * roots[k * step];
                        values[start + k] = even + odd;
                        values[start + k + half] = even - odd;
                    }
                }
            }
        }
    }

    public static string Run() {
        checked {
            const int N = 65536;
            var signal = new Complex[N];
            var forward = new Complex[N / 2];
            var inverse = new Complex[N / 2];
            for (long i = 0; i < N; i++) {
                signal[i] = new Complex(i * 7919 % 1009 / 1009.0, i * 104729 % 757 / 757.0);
            }

            for (var i = 0; i < N / 2; i++) {
                forward[i] = Complex.FromPolarCoordinates(1.0, -2.0 * Math.PI * i / N);
                inverse[i] = Complex.Conjugate(forward[i]);
            }

            var values = (Complex[])signal.Clone();
            var peak = 0.0;
            for (var round = 0; round < 16; round++) {
                Transform(values, forward);
                peak = values[0].Magnitude;
                Transform(values, inverse);
                for (var i = 0; i < N; i++) {
                    values[i] /= N;
                }
            }

            var error = 0.0;
            for (var i = 0; i < N; i++) {
                error += (values[i] - signal[i]).Magnitude;
            }

            return $"{peak:F3} {(error < 0.000001 ? "true" : "false")}";
        }
    }
}
