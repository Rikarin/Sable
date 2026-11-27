// Port of region.sb: .NET has no region, so each case is the chain of
// records the collector allocates, a class a record, as "heap" is.

[Benchmark("library/region", "heap")]
[Benchmark("library/region", "region")]
[Benchmark("library/region", "frames")]
static class LibraryRegion {
    sealed class Node(long value, Node? next) {
        public readonly long Value = value;
        public readonly Node? Next = next;
    }

    const int Records = 2000;
    const int Frames = 100;

    static long Frame(long frame) {
        checked {
            Node? head = null;
            for (long i = 0; i < Records; i++) {
                head = new Node(i + frame, head);
            }

            long total = 0;
            for (var node = head; node is not null; node = node.Next) {
                total += node.Value;
            }

            return total;
        }
    }

    public static Func<string> Setup(string how) =>
        () => {
            checked {
                long total = 0;
                for (long frame = 0; frame < Frames; frame++) {
                    total += Frame(frame);
                }

                return total.ToString();
            }
        };
}
