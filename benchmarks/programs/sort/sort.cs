// Port of sort.sb.
[Benchmark("programs/sort")]
static class ProgramsSort {
    static List<T> Sorted<T>(ReadOnlySpan<T> items, Func<T, T, bool> before) {
        if (items.Length <= 1) {
            var copy = new List<T>();
            foreach (var item in items) {
                copy.Add(item);
            }

            return copy;
        }

        var middle = items.Length / 2;
        var left = Sorted(items[..middle], before);
        var right = Sorted(items[middle..], before);
        var result = new List<T>();
        int i = 0, j = 0;
        while (i < left.Count && j < right.Count) {
            result.Add(before(right[j], left[i]) ? right[j++] : left[i++]);
        }

        while (i < left.Count) {
            result.Add(left[i++]);
        }

        while (j < right.Count) {
            result.Add(right[j++]);
        }

        return result;
    }

    public static string Run() {
        checked {
            var numbers = new List<long>();
            long seed = 42;
            for (var i = 0; i < 200000; i++) {
                seed = (seed * 1103515245 + 12345) % 2147483648;
                numbers.Add(seed % 1000000);
            }

            var ascending = Sorted<long>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(numbers), (a, b) => a < b);
            var labels = new List<string>();
            for (var i = 0; i < 20000; i++) {
                labels.Add($"item-{ascending[i * 10] % 997}");
            }

            var byLabel = Sorted<string>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(labels), (a, b) => a.Length < b.Length);
            return $"{ascending[0]}\n{ascending[199999]}\n{byLabel[0]}\n{byLabel[19999]}";
        }
    }
}
