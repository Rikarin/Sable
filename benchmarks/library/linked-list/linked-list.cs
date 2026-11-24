// Port of linked-list.sb.
[Benchmark("library/linked-list")]
static class LibraryLinkedList {
    public static string Run() {
        checked {
            var list = new LinkedList<long>();
            for (long i = 0; i < 500000; i++) {
                list.AddLast(i);
            }

            long total = 0;
            for (var round = 0; round < 10; round++) {
                foreach (var value in list) {
                    total += value;
                }
            }

            for (var i = 0; i < 2000000; i++) {
                var value = list.First!.Value;
                list.RemoveFirst();
                total += value;
                list.AddLast(value);
            }

            return $"{list.Count} {total}";
        }
    }
}
