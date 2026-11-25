// Port of immutable-list.sb.
using System.Collections.Immutable;

[Benchmark("library/immutable-list")]
static class LibraryImmutableList {
    public static string Run() {
        checked {
            var builder = ImmutableList.CreateBuilder<long>();
            for (long i = 0; i < 300000; i++) {
                builder.Add(i);
            }

            var list = builder.ToImmutable();
            long total = 0;
            for (long i = 0; i < 600000; i++) {
                total += list[(int)(i * 7919 % 300000)];
            }

            foreach (var element in list) {
                total += element;
            }

            var versions = new List<ImmutableList<long>>();
            var current = list;
            for (long i = 0; i < 60000; i++) {
                current = current.SetItem((int)(i * 7919 % 300000), i);
                if (i % 1000 == 0) {
                    versions.Add(current);
                }
            }

            foreach (var version in versions) {
                total += version[7919];
            }

            builder = list.ToBuilder();
            for (long i = 0; i < 60000; i++) {
                builder.Insert((int)(i * 104729 % builder.Count), i);
            }

            for (long i = 0; i < 60000; i++) {
                var at = (int)(i * 104729 % builder.Count);
                total += builder[at];
                builder.RemoveAt(at);
            }

            list = builder.ToImmutable();
            for (long i = 0; i < 20; i++) {
                var cut = (int)(i * 7919 % 200000);
                list = list.GetRange(cut, list.Count - cut).AddRange(list.GetRange(0, cut));
            }

            return $"{total} {list.Count} {list[12345]}";
        }
    }
}
