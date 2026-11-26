// Port of object-pool.sb: a StringBuilder of 256 made for each record, or
// rented from Microsoft.Extensions.ObjectPool's DefaultObjectPool with the
// StringBuilder policy.
using System.Text;
using Microsoft.Extensions.ObjectPool;

[Benchmark("library/object-pool", "fresh")]
[Benchmark("library/object-pool", "pooled")]
static class LibraryObjectPool {
    static string Record(StringBuilder builder, long i) {
        checked {
            builder.Append("id=");
            builder.Append(i);
            builder.Append(", name=item");
            builder.Append(i % 100);
            builder.Append(", total=");
            builder.Append(i * 3);
            builder.Append(';');
            return builder.ToString();
        }
    }

    public static Func<string> Setup(string how) {
        var pool = new DefaultObjectPool<StringBuilder>(new StringBuilderPooledObjectPolicy { InitialCapacity = 256 }, 16);
        if (how == "fresh") {
            return () => {
                long bytes = 0;
                for (long i = 0; i < 100_000; i++) {
                    bytes += Encoding.UTF8.GetByteCount(Record(new StringBuilder(256), i));
                }

                return bytes.ToString();
            };
        }

        return () => {
            long bytes = 0;
            for (long i = 0; i < 100_000; i++) {
                var builder = pool.Get();
                try {
                    bytes += Encoding.UTF8.GetByteCount(Record(builder, i));
                } finally {
                    pool.Return(builder);
                }
            }

            return bytes.ToString();
        };
    }
}
