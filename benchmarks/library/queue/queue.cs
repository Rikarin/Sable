// Port of queue.sb.
[Benchmark("library/queue")]
static class LibraryQueue {
    public static string Run() {
        checked {
            var queue = new Queue<long>();
            long total = 0;
            for (long i = 0; i < 1000; i++) {
                queue.Enqueue(i);
            }

            for (long i = 0; i < 5000000; i++) {
                queue.Enqueue(i);
                total += queue.Dequeue();
            }

            for (long i = 0; i < 1000000; i++) {
                queue.Enqueue(i);
            }

            while (queue.TryDequeue(out var value)) {
                total += value;
            }

            return total.ToString();
        }
    }
}
