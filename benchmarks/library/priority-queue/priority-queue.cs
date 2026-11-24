// Port of priority-queue.sb.
[Benchmark("library/priority-queue")]
static class LibraryPriorityQueue {
    public static string Run() {
        checked {
            var queue = new PriorityQueue<long, long>();
            long x = 12345;
            long total = 0;
            for (var i = 0; i < 300000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                queue.Enqueue(x, x);
            }

            while (queue.TryDequeue(out var value, out _)) {
                total = (total * 31 + value) % 1000000007;
            }

            for (var i = 0; i < 1000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                queue.Enqueue(x, x);
            }

            for (var i = 0; i < 1000000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                queue.Enqueue(x, x);
                total = (total * 31 + queue.Dequeue()) % 1000000007;
            }

            return total.ToString();
        }
    }
}
