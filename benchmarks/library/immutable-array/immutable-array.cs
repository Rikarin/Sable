// Port of immutable-array.sb.
using System.Collections.Immutable;

[Benchmark("library/immutable-array")]
static class LibraryImmutableArray {
    public static string Run() {
        checked {
            long total = 0;
            for (var round = 0; round < 20; round++) {
                var builder = ImmutableArray.CreateBuilder<long>();
                for (var i = 0; i < 100000; i++) {
                    builder.Add(i ^ round);
                }

                var array = builder.ToImmutable();
                foreach (var element in array) {
                    total += element;
                }

                for (var i = 0; i < 50; i++) {
                    var changed = array.SetItem(i * 1999, i).Add(i);
                    total += changed[i * 1999] + changed.Length;
                }
            }

            return $"{total}";
        }
    }
}
