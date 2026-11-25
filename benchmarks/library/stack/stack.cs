// Port of stack.sb.
[Benchmark("library/stack")]
static class LibraryStack {
    public static string Run() {
        checked {
            var stack = new Stack<long>();
            long total = 0;
            for (long i = 0; i < 1000; i++) {
                stack.Push(i);
            }

            for (long i = 0; i < 5000000; i++) {
                stack.Push(i);
                total += stack.Pop();
            }

            for (long i = 0; i < 2000000; i++) {
                stack.Push(i);
            }

            while (stack.TryPop(out var value)) {
                total += value;
            }

            return total.ToString();
        }
    }
}
