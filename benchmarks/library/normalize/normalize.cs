// Port of normalize.sb.
[Benchmark("library/normalize")]
static class LibraryNormalize {
    public static Func<string> Setup() {
        checked {
            var accented = string.Concat(Enumerable.Repeat("Ça été où à la fête, señor Größe: Ärger über Öl. ", 20000));
            var ascii = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog, again. ", 20000));
            return () => Run(accented, ascii);
        }
    }

    static string Run(string accented, string ascii) {
        checked {
            long total = 0;
            for (var round = 0; round < 3; round++) {
                var apart = accented.Normalize(System.Text.NormalizationForm.FormD);
                total += apart.Length;
                if (apart.Normalize(System.Text.NormalizationForm.FormC) == accented) { total++; }
                total += ascii.Normalize(System.Text.NormalizationForm.FormC).Length;
            }

            return total.ToString();
        }
    }
}
