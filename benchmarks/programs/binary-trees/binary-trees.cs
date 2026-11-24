// Port of binary-trees.sb.
[Benchmark("programs/binary-trees")]
static class ProgramBinaryTrees {
    sealed class Node(Node? left, Node? right) {
        public readonly Node? Left = left;
        public readonly Node? Right = right;
    }

    static Node Build(long depth) => checked(depth == 0 ? new Node(null, null) : new Node(Build(depth - 1), Build(depth - 1)));

    static long Check(Node node) {
        checked {
            long total = 1;
            if (node.Left is { } left) {
                total += Check(left);
            }

            if (node.Right is { } right) {
                total += Check(right);
            }

            return total;
        }
    }

    public static string Run() {
        checked {
            const int maxDepth = 16;
            var lines = new List<string>();
            var longLived = Build(maxDepth);
            for (var depth = 4; depth <= maxDepth; depth += 2) {
                var iterations = 1L << (maxDepth - depth + 4);
                long sum = 0;
                for (long i = 0; i < iterations; i++) {
                    sum += Check(Build(depth));
                }

                lines.Add($"{iterations} trees of depth {depth}: {sum}");
            }

            lines.Add($"long lived tree of depth {maxDepth}: {Check(longLived)}");
            return string.Join('\n', lines);
        }
    }
}
