// Port of linq.sb.
[Benchmark("library/linq")]
static class LibraryLinq {
    public static string Run() {
        checked {
            var values = new List<long>();
            for (long i = 0; i < 10000000; i++) {
                values.Add(i);
            }

            long chained = 0;
            for (long round = 0; round < 5; round++) {
                var modulus = round + 3;
                chained += values.Where(v => v % modulus != 0).Select(v => v * 2).Sum();
            }

            long looped = 0;
            for (long round = 0; round < 5; round++) {
                var modulus = round + 3;
                foreach (var value in values) {
                    if (value % modulus != 0) {
                        looped += value * 2;
                    }
                }
            }

            return $"{chained} {looped}";
        }
    }
}
