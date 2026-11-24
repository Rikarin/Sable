// Port of list-edit.sb.
[Benchmark("library/list-edit")]
static class LibraryListEdit {
    public static string Run() {
        checked {
            long total = 0;
            for (long round = 0; round < 3; round++) {
                var list = new List<long>();
                for (long i = 0; i < 1000000; i++) {
                    list.Add(i ^ round);
                }

                for (var i = 0; i < list.Count; i++) {
                    total += list[i] % 7;
                }

                list.RemoveRange(20000, list.Count - 20000);
                for (long i = 0; i < 2000; i++) {
                    list.Insert(list.Count / 2, i);
                }

                for (var i = 0; i < 2000; i++) {
                    var at = list.Count / 3;
                    total += list[at];
                    list.RemoveAt(at);
                }

                list.RemoveAll(x => x % 2 == 0);
                total += list.Count + list[list.Count / 2];
            }

            return total.ToString();
        }
    }
}
