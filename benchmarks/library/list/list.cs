// Port of list.sb.
[Benchmark("library/list")]
static class LibraryList {
    public static string Run() {
        checked {
            long total = 0;
            for (long round = 0; round < 3; round++) {
                var values = new List<long>();
                for (long i = 0; i < 10000000; i++) {
                    values.Add(i ^ round);
                }

                foreach (var v in values) {
                    total += v;
                }

                for (var i = 0; i < values.Count; i++) {
                    total += values[i] % 7;
                }

                while (values.Count > 5000000) {
                    total -= values[^1];
                    values.RemoveAt(values.Count - 1);
                }

                total += values.Count;
            }

            return total.ToString();
        }
    }
}
