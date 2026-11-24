// Port of shuffle.sb.
[Benchmark("library/shuffle")]
static class LibraryShuffle {
    public static string Run() {
        checked {
            var random = new Random();
            var values = new long[100000];
            for (var i = 0; i < values.Length; i++) {
                values[i] = i;
            }

            for (var i = 0; i < 50; i++) {
                random.Shuffle(values);
            }

            long total = 0;
            foreach (var value in values) {
                total += value;
            }

            return $"{total}";
        }
    }
}
