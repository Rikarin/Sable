// The .NET side of the Phase 4 exit criterion "Dictionary/List/String within
// 15% of their .NET equivalents" (docs/04 §4.4): each benchmark here is a
// port of ../versus-dotnet/<name>.sb — the same steps, sizes and output —
// and each 'programs/<name>' (Programs.cs) of ../<name>.sb.
//
//     dotnet run -c Release -- <name> [runs]
//
// Runs the benchmark for a second and at least once, so that the JIT has
// tiered up even a short one, then 'runs' times more (default 5), and prints its
// output once, then 'ms <fastest>' on standard error (and 'times', each
// run's): the processor time
// (user and system) of the fastest run — the steady state, after the JIT has
// tiered up, which is the hardest .NET time to match. Processor time rather
// than wall time, as tools/bench/versus_dotnet.py measures Sable, so that a
// busy machine skews both sides less. Integer arithmetic is checked, as
// Sable's is.

using System.Diagnostics;

var benchmarks = new Dictionary<string, Func<string>> {
    ["map-int"] = MapInt,
    ["map-string"] = MapString,
    ["list"] = List,
    ["list-edit"] = ListEdit,
    ["sort"] = Sort,
    ["string"] = Strings,
    ["text-search"] = TextSearch,
    ["utf8-validate"] = Utf8Validate,
    ["linq"] = Linq,
    ["mutex"] = Mutex,
    ["concurrent-dictionary"] = ConcurrentDictionaryBench,
    ["concurrent-queue"] = ConcurrentQueueBench,
    ["sorted-dictionary"] = SortedDictionaryBench,
    ["text-builder"] = TextBuilding,
    ["tensor-primitives"] = TensorPrimitivesBench,
    ["decimal"] = Decimals,
    ["bigint-multiply"] = BigIntMultiply,
    ["bigint-divide"] = BigIntDivide,
    ["bigint-tostring"] = BigIntToString,
    ["serialize"] = Serializing,
    ["programs/binary-trees"] = Programs.BinaryTrees,
    ["programs/nbody"] = Programs.NBody,
    ["programs/shapes"] = Programs.Shapes,
    ["programs/sort"] = Programs.Sort,
    ["programs/words"] = Programs.Words,
};

if (args.Length == 0 || !benchmarks.TryGetValue(args[0], out var run)) {
    Console.Error.WriteLine($"usage: <{string.Join("|", benchmarks.Keys)}> [runs]");
    return 2;
}

var runs = args.Length > 1 ? int.Parse(args[1]) : 5;
string? output = null;
var times = new List<double>();
var clock = Stopwatch.StartNew();
do {
    run();
} while (clock.ElapsedMilliseconds < 1000);

for (var i = 0; i < runs; i++) {
    // Not Process.TotalProcessorTime, which on Linux counts ticks of 10 ms.
    var before = Environment.CpuUsage.TotalTime;
    output = run();
    times.Add((Environment.CpuUsage.TotalTime - before).TotalMilliseconds);
}

Console.WriteLine(output);
Console.Error.WriteLine($"ms {times.Min():F1}");
Console.Error.WriteLine($"times {string.Join(' ', times.Select(ms => ms.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}");
return 0;

// Four threads each taking the lock a quarter of a million times to add one
// to a shared count: .NET 11's Lock, through the 'lock' statement.
static string Mutex() {
    var gate = new Lock();
    long count = 0;
    var threads = new Thread[4];
    for (var t = 0; t < threads.Length; t++) {
        threads[t] = new Thread(() => {
            for (var i = 0; i < 250000; i++) {
                lock (gate) {
                    count++;
                }
            }
        });
        threads[t].Start();
    }

    foreach (var thread in threads) {
        thread.Join();
    }

    return count.ToString();
}

// Four threads each counting a quarter of a million keys of 4096 with
// AddOrUpdate and reading one back with GetOrAdd.
static string ConcurrentDictionaryBench() {
    checked {
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();
        var threads = new Thread[4];
        for (var t = 0; t < threads.Length; t++) {
            var seed = t;
            threads[t] = new Thread(() => {
                long x = seed * 7919 + 1;
                for (var i = 0; i < 250000; i++) {
                    x = (x * 1103515245 + 12345) % 2147483648;
                    var key = x % 4096;
                    counts.AddOrUpdate(key, 1, static (_, v) => v + 1);
                    counts.GetOrAdd(key + 4096, static _ => 1);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads) {
            thread.Join();
        }

        long total = 0;
        foreach (var entry in counts) {
            total += entry.Value;
        }

        return $"{counts.Count} {total}";
    }
}

// Two threads enqueue half a million numbers each while two dequeue them
// all and add them up.
static string ConcurrentQueueBench() {
    checked {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<long>();
        long taken = 0;
        var sums = new long[2];
        var threads = new List<Thread>();
        for (var p = 0; p < 2; p++) {
            threads.Add(new Thread(() => {
                for (long i = 0; i < 500000; i++) {
                    queue.Enqueue(i);
                }
            }));
        }

        for (var c = 0; c < 2; c++) {
            var consumer = c;
            threads.Add(new Thread(() => {
                long total = 0;
                while (Interlocked.Read(ref taken) < 1000000) {
                    if (queue.TryDequeue(out var value)) {
                        total += value;
                        Interlocked.Increment(ref taken);
                    }
                }

                sums[consumer] = total;
            }));
        }

        foreach (var thread in threads) {
            thread.Start();
        }

        foreach (var thread in threads) {
            thread.Join();
        }

        return (sums[0] + sums[1]).ToString();
    }
}

// 200,000 pseudo-random keys inserted into a SortedDictionary, each looked
// up, a third removed, then the rest gone through in order and the keys of
// a range counted (GetViewBetween's count, as SortedSet has no ranged view
// on SortedDictionary: the keys are walked in order as Sable's range does).
static string SortedDictionaryBench() {
    checked {
        const int n = 200000;
        var keys = new long[n];
        long x = 12345;
        for (var i = 0; i < n; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            keys[i] = x;
        }

        var d = new SortedDictionary<long, long>();
        for (var i = 0; i < n; i++) {
            d[keys[i]] = i;
        }

        long found = 0;
        for (var i = 0; i < n; i++) {
            if (d.TryGetValue(keys[i], out var value)) {
                found += value % 7;
            }
        }

        for (var i = 0; i < n; i++) {
            if (i % 3 == 0) {
                d.Remove(keys[i]);
            }
        }

        long total = 0;
        long previous = -1;
        var ordered = true;
        foreach (var entry in d) {
            if (entry.Key <= previous) {
                ordered = false;
            }

            previous = entry.Key;
            total = (total + entry.Value) % 1000000007;
        }

        long inRange = 0;
        foreach (var entry in d) {
            if (entry.Key >= 500000000 && entry.Key < 1000000000) {
                inRange++;
            }
        }

        return $"{d.Count} {found} {total} {inRange} {(ordered ? "true" : "false")}";
    }
}

static string MapInt() {
    checked {
        const int n = 1000000;
        var keys = new List<long>();
        long x = 12345;
        for (var i = 0; i < n; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            keys.Add(x);
        }

        var map = new Dictionary<long, long>();
        for (var i = 0; i < n; i++) {
            map[keys[i]] = i;
        }

        long sum = 0;
        for (var round = 0; round < 3; round++) {
            foreach (var key in keys) {
                sum += map.TryGetValue(key, out var value) ? value : 0;
            }
        }

        long misses = 0;
        for (var i = 0; i < n; i++) {
            if (!map.ContainsKey(keys[i] + 2147483648)) {
                misses++;
            }
        }

        for (var i = 0; i < n; i += 2) {
            map.Remove(keys[i]);
        }

        long hits = 0;
        foreach (var key in keys) {
            if (map.ContainsKey(key)) {
                hits++;
            }
        }

        return $"{map.Count} {sum} {misses} {hits}";
    }
}

static string MapString() {
    checked {
        const int n = 200000;
        var keys = new List<string>();
        for (long i = 0; i < n; i++) {
            keys.Add($"key-{i * 7919 % 1000003}");
        }

        var map = new Dictionary<string, long>();
        for (var i = 0; i < n; i++) {
            map[keys[i]] = i;
        }

        long sum = 0;
        for (var round = 0; round < 5; round++) {
            foreach (var key in keys) {
                sum += map.TryGetValue(key, out var value) ? value : 0;
            }
        }

        for (var i = 0; i < n; i += 2) {
            map.Remove(keys[i]);
        }

        long hits = 0;
        foreach (var key in keys) {
            if (map.ContainsKey(key)) {
                hits++;
            }
        }

        return $"{map.Count} {sum} {hits}";
    }
}

static string List() {
    checked {
        long total = 0;
        for (long round = 0; round < 3; round++) {
            var values = new List<long>();
            for (long i = 0; i < 10000000; i++) {
                values.Add(i ^ round);
            }

            foreach (var v in values) {
                total += v;
            }

            for (var i = 0; i < values.Count; i++) {
                total += values[i] % 7;
            }

            while (values.Count > 5000000) {
                total -= values[^1];
                values.RemoveAt(values.Count - 1);
            }

            total += values.Count;
        }

        return total.ToString();
    }
}

static string Linq() {
    checked {
        var values = new List<long>();
        for (long i = 0; i < 10000000; i++) {
            values.Add(i);
        }

        long chained = 0;
        for (long round = 0; round < 5; round++) {
            var modulus = round + 3;
            chained += values.Where(v => v % modulus != 0).Select(v => v * 2).Sum();
        }

        long looped = 0;
        for (long round = 0; round < 5; round++) {
            var modulus = round + 3;
            foreach (var value in values) {
                if (value % modulus != 0) {
                    looped += value * 2;
                }
            }
        }

        return $"{chained} {looped}";
    }
}

static string ListEdit() {
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

static string Sort() {
    checked {
        var numbers = new List<long>();
        long x = 42;
        for (var i = 0; i < 1000000; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            numbers.Add(x);
        }

        var words = new List<string>();
        for (var i = 0; i < 200000; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            words.Add($"w{x % 1000000}");
        }

        // Sable orders strings by Unicode scalar, as ordinal comparison does for these.
        var a = new List<long>(numbers);
        a.Sort();
        var b = new List<string>(words);
        b.Sort(string.CompareOrdinal);
        var ordered = true;
        for (var i = 1; i < a.Count; i++) {
            if (a[i] < a[i - 1]) { ordered = false; }
        }

        for (var i = 1; i < b.Count; i++) {
            if (string.CompareOrdinal(b[i], b[i - 1]) < 0) { ordered = false; }
        }

        return $"{(ordered ? "true" : "false")} {a[0]} {a[a.Count / 2]} {b[0]} {b[^1]}";
    }
}

// A million invoice lines in decimal: a price in cents times a quantity, a
// discount rate taken off and rounded to the cent (Math.Round's default,
// ties to even, as Sable's rounded(scale:) is), the line added to a total;
// and each line split three ways, rounded to four places.
static string Decimals() {
    checked {
        var total = 0m;
        var shares = 0m;
        var three = 3m;
        long x = 42;
        for (var i = 0; i < 1000000; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            var price = new decimal((int)(x % 100000), 0, 0, false, 2);
            var quantity = (decimal)(x % 7 + 1);
            var rate = new decimal((int)(x % 250), 0, 0, false, 3);
            var line = price * quantity;
            var net = line - Math.Round(line * rate, 2);
            total += net;
            shares += Math.Round(net / three, 4);
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return $"{total.ToString(invariant)} {shares.ToString(invariant)}";
    }
}

// Two BigIntegers of about 10^5 decimal digits (3^209590 and 7^118330),
// multiplied ten times over.
static string BigIntMultiply() {
    checked {
        var a = System.Numerics.BigInteger.Pow(3, 209590);
        var b = System.Numerics.BigInteger.Pow(7, 118330);
        long bits = 0;
        ulong low = 0;
        for (var i = 0; i < 10; i++) {
            var product = a * (b + i);
            bits += (long)product.GetBitLength();
            low ^= (ulong)(product & ulong.MaxValue);
        }

        return $"{bits} {low}";
    }
}

// A BigInteger of 10^5 decimal digits divided by one of half as many, ten
// times over, for the quotient and the remainder at once.
static string BigIntDivide() {
    checked {
        var a = System.Numerics.BigInteger.Pow(3, 209590);
        var b = System.Numerics.BigInteger.Pow(7, 59165);
        long bits = 0;
        ulong low = 0;
        for (var i = 0; i < 10; i++) {
            var (quotient, remainder) = System.Numerics.BigInteger.DivRem(a + i, b);
            bits += (long)quotient.GetBitLength();
            low ^= (ulong)(remainder & ulong.MaxValue);
        }

        return $"{bits} {low}";
    }
}

// A BigInteger of 10^5 digits (3^209590) in decimal, five times over.
static string BigIntToString() {
    checked {
        var a = System.Numerics.BigInteger.Pow(3, 209590);
        long total = 0;
        var text = "";
        for (var i = 0; i < 5; i++) {
            text = (a + i).ToString();
            total += text.Length;
        }

        return $"{total} {text[..12]} {text[^12..]}";
    }
}

// A million small appends to a StringBuilder, built once; then edits in the
// middle, a removal at the front and a replacement throughout.
static string TextBuilding() {
    checked {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 1000000; i++) {
            builder.Append("item ");
            builder.Append(i);
            builder.Append(';');
            if (i % 10 == 0) {
                builder.Append('\n');
            }
        }

        var built = builder.ToString();
        for (var j = 0; j < 20; j++) {
            builder.Insert(builder.Length / 2, "<>");
        }

        builder.Remove(0, 5);
        builder.Replace("item", "it");
        var edited = builder.ToString();
        return $"{built.Length} {edited.Length}";
    }
}

// A hundred thousand orders of three items each, written to JSON text and
// read back through System.Text.Json's source-generated serializers.
static string Serializing() {
    checked {
        var orders = new List<Order>();
        for (var i = 0; i < 100000; i++) {
            var items = new List<Item>();
            for (var k = 0; k < 3; k++) {
                items.Add(new Item($"SKU-{i % 977}-{k}", (i + k) % 7 + 1, (i * 3 + k) % 1000 + 0.25));
            }

            orders.Add(new Order(i, $"customer {i % 5003}", i % 3 == 0, i % 4 == 0 ? "leave at the door" : null, items));
        }

        var text = System.Text.Json.JsonSerializer.Serialize(orders, OrdersJson.Default.ListOrder);
        var back = System.Text.Json.JsonSerializer.Deserialize(text, OrdersJson.Default.ListOrder)!;
        long quantity = 0;
        foreach (var order in back) {
            foreach (var item in order.Items) {
                quantity += item.Quantity;
            }
        }

        return $"{System.Text.Encoding.UTF8.GetByteCount(text)} {back.Count} {quantity}";
    }
}

static string Strings() {
    checked {
        string[] names = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
        var lines = new List<string>();
        for (var i = 0; i < 300000; i++) {
            lines.Add($"{names[i % 8]} {i} {names[i * 3 % 8]} end");
        }

        long characters = 0;
        long matches = 0;
        long words = 0;
        foreach (var line in lines) {
            foreach (var word in line.Split(' ')) {
                words++;
                characters += word.Length;
                if (word == "gamma") {
                    matches++;
                }
            }
        }

        long joinedLength = 0;
        for (var i = 0; i < lines.Count; i += 16) {
            var block = "";
            for (var j = 0; j < 16 && i + j < lines.Count; j++) {
                block = block + lines[i + j] + "\n";
            }

            joinedLength += block.Length;
        }

        long sliced = 0;
        foreach (var line in lines) {
            if (line[..4] == "alph") {
                sliced++;
            }
        }

        return $"{words} {characters} {matches} {joinedLength} {sliced}";
    }
}

static string TextSearch() {
    checked {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 20000; i++) {
            builder.Append("lorem ipsum dolor sit amet, consectetur adipiscing elit, ");
        }

        builder.Append("needle");
        var text = builder.ToString();

        long found = 0;
        long pieces = 0;
        for (var round = 0; round < 200; round++) {
            var at = text.IndexOf("needle", StringComparison.Ordinal);
            if (at >= 0) {
                found += at;
            }

            if (text.IndexOf("haystack", StringComparison.Ordinal) < 0) {
                found++;
            }

            pieces += text.Split(", ").Length;
        }

        return $"{found} {pieces}";
    }
}

// Sum, dot, cosine similarity, add, multiply and softmax over 4096 floats,
// System.Numerics.Tensors' TensorPrimitives: twenty thousand rounds (two
// thousand of softmax), one element changed a round.
static string TensorPrimitivesBench() {
    checked {
        const int n = 4096;
        var x = new float[n];
        var y = new float[n];
        for (var i = 0; i < n; i++) {
            x[i] = i % 7 - 3;
            y[i] = i % 5 - 2;
        }

        var sums = (float[])y.Clone();
        var products = (float[])y.Clone();
        var probabilities = (float[])y.Clone();
        var total = 0.0;
        var cosine = 0.0;
        for (var round = 0; round < 20000; round++) {
            x[round % n] = round % 7 - 3;
            total += (double)System.Numerics.Tensors.TensorPrimitives.Sum<float>(x) + System.Numerics.Tensors.TensorPrimitives.Dot<float>(x, y);
            cosine += System.Numerics.Tensors.TensorPrimitives.CosineSimilarity<float>(x, y);
            System.Numerics.Tensors.TensorPrimitives.Add<float>(x, y, sums);
            System.Numerics.Tensors.TensorPrimitives.Multiply<float>(sums, y, products);
            total += products[round % n];
        }

        var spread = 0.0;
        for (var round = 0; round < 2000; round++) {
            y[round % n] = round % 5 - 2;
            System.Numerics.Tensors.TensorPrimitives.SoftMax<float>(y, probabilities);
            spread += probabilities[round % n];
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Format(inv, "{0:F1} {1:F3} {2:F1} {3:F4}", total, cosine, (double)System.Numerics.Tensors.TensorPrimitives.Sum<float>(products), spread);
    }
}

static string Utf8Validate() {
    checked {
        var ascii = new System.Text.StringBuilder();
        var mixed = new System.Text.StringBuilder();
        for (var i = 0; i < 16384; i++) {
            ascii.Append("The quick brown fox jumps over the lazy dog, again. ");
            mixed.Append("Grüße, мир, 世界 😀 and plain words too. ");
        }

        var asciiBytes = System.Text.Encoding.UTF8.GetBytes(ascii.ToString());
        var mixedBytes = System.Text.Encoding.UTF8.GetBytes(mixed.ToString());

        long valid = 0;
        for (var round = 0; round < 400; round++) {
            if (System.Text.Unicode.Utf8.IsValid(asciiBytes)) {
                valid++;
            }

            if (System.Text.Unicode.Utf8.IsValid(mixedBytes)) {
                valid++;
            }
        }

        return $"{valid} {asciiBytes.Length} {mixedBytes.Length}";
    }
}

record Item(string Sku, long Quantity, double Price);

record Order(long Id, string Customer, bool Paid, string? Note, List<Item> Items);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<Order>))]
partial class OrdersJson : System.Text.Json.Serialization.JsonSerializerContext;
