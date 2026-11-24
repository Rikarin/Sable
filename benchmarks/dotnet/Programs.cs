using System.Globalization;

// The .NET ports of ../<name>.sb, run as 'programs/<name>': the same
// algorithms, sizes and output as the Sable programs, in ordinary C#.
// Integer arithmetic is checked, as Sable's is. orders has none: what it
// measures, inline layout, .NET has no equal of.
static class Programs {
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

    public static string BinaryTrees() {
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

    struct Body(double x, double y, double z, double vx, double vy, double vz, double mass) {
        public double X = x, Y = y, Z = z, Vx = vx, Vy = vy, Vz = vz, Mass = mass;
    }

    static double SqrtApprox(double v) {
        var guess = v / 2.0 + 0.5;
        for (var k = 0; k < 12; k++) {
            guess = 0.5 * (guess + v / guess);
        }

        return guess;
    }

    static double Energy(List<Body> bodies) {
        var e = 0.0;
        for (var i = 0; i < bodies.Count; i++) {
            var b = bodies[i];
            e += 0.5 * b.Mass * (b.Vx * b.Vx + b.Vy * b.Vy + b.Vz * b.Vz);
            for (var j = i + 1; j < bodies.Count; j++) {
                var c = bodies[j];
                var dx = b.X - c.X;
                var dy = b.Y - c.Y;
                var dz = b.Z - c.Z;
                e -= b.Mass * c.Mass / SqrtApprox(dx * dx + dy * dy + dz * dz);
            }
        }

        return e;
    }

    static void Advance(List<Body> bodies, double dt) {
        var n = bodies.Count;
        for (var i = 0; i < n; i++) {
            var b = bodies[i];
            for (var j = i + 1; j < n; j++) {
                var c = bodies[j];
                var dx = b.X - c.X;
                var dy = b.Y - c.Y;
                var dz = b.Z - c.Z;
                var d2 = dx * dx + dy * dy + dz * dz;
                var mag = dt / (d2 * SqrtApprox(d2));
                b.Vx -= dx * c.Mass * mag;
                b.Vy -= dy * c.Mass * mag;
                b.Vz -= dz * c.Mass * mag;
                c.Vx += dx * b.Mass * mag;
                c.Vy += dy * b.Mass * mag;
                c.Vz += dz * b.Mass * mag;
                bodies[j] = c;
            }

            b.X += dt * b.Vx;
            b.Y += dt * b.Vy;
            b.Z += dt * b.Vz;
            bodies[i] = b;
        }
    }

    public static string NBody() {
        var bodies = new List<Body> {
            new(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 39.47),
            new(4.84, -1.16, -0.10, 0.60, 2.81, -0.02, 0.037),
            new(8.34, 4.12, -0.40, -1.01, 1.82, 0.008, 0.011),
            new(12.89, -15.11, -0.22, 1.08, 0.86, -0.01, 0.0017),
            new(15.37, -25.91, 0.17, 0.97, 0.59, -0.03, 0.002),
        };
        var before = Energy(bodies);
        for (var step = 0; step < 200000; step++) {
            Advance(bodies, 0.01);
        }

        return string.Create(CultureInfo.InvariantCulture, $"{before}\n{Energy(bodies)}");
    }

    interface IShape {
        double Area();
        string Name();
    }

    readonly struct Circle(double r) : IShape {
        public double Area() => 3.14159 * r * r;
        public string Name() => "circle";
    }

    readonly struct Rect(double w, double h) : IShape {
        public double Area() => w * h;
        public string Name() => "rect";
    }

    class Animal(long legs) {
        public readonly long Legs = legs;
        public virtual long Weight() => Legs;
    }

    sealed class Bird() : Animal(2) {
        public override long Weight() => 1;
    }

    sealed class Dog() : Animal(4) {
        public override long Weight() => 20;
    }

    public static string Shapes() {
        checked {
            var shapes = new List<IShape>();
            var animals = new List<Animal>();
            for (var i = 0; i < 1000; i++) {
                if (i % 2 == 0) {
                    shapes.Add(new Circle(i % 10));
                    animals.Add(new Bird());
                } else {
                    shapes.Add(new Rect(i % 7, 2.0));
                    animals.Add(new Dog());
                }
            }

            var area = 0.0;
            long letters = 0;
            long weight = 0;
            for (var round = 0; round < 300; round++) {
                foreach (var shape in shapes) {
                    area += shape.Area();
                    letters += shape.Name().Length;
                }

                foreach (var animal in animals) {
                    weight += animal.Weight() + animal.Legs;
                }
            }

            return string.Create(CultureInfo.InvariantCulture, $"{area}\n{letters}\n{weight}");
        }
    }

    static List<T> Sorted<T>(ReadOnlySpan<T> items, Func<T, T, bool> before) {
        if (items.Length <= 1) {
            var copy = new List<T>();
            foreach (var item in items) {
                copy.Add(item);
            }

            return copy;
        }

        var middle = items.Length / 2;
        var left = Sorted(items[..middle], before);
        var right = Sorted(items[middle..], before);
        var result = new List<T>();
        int i = 0, j = 0;
        while (i < left.Count && j < right.Count) {
            result.Add(before(right[j], left[i]) ? right[j++] : left[i++]);
        }

        while (i < left.Count) {
            result.Add(left[i++]);
        }

        while (j < right.Count) {
            result.Add(right[j++]);
        }

        return result;
    }

    public static string Sort() {
        checked {
            var numbers = new List<long>();
            long seed = 42;
            for (var i = 0; i < 200000; i++) {
                seed = (seed * 1103515245 + 12345) % 2147483648;
                numbers.Add(seed % 1000000);
            }

            var ascending = Sorted<long>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(numbers), (a, b) => a < b);
            var labels = new List<string>();
            for (var i = 0; i < 20000; i++) {
                labels.Add($"item-{ascending[i * 10] % 997}");
            }

            var byLabel = Sorted<string>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(labels), (a, b) => a.Length < b.Length);
            return $"{ascending[0]}\n{ascending[199999]}\n{byLabel[0]}\n{byLabel[19999]}";
        }
    }

    record struct WordCount(string Word, long Count);

    static List<string> Corpus(int lines) {
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa"];
        var result = new List<string>();
        for (var i = 0; i < lines; i++) {
            var a = words[i % 10];
            var b = words[(i * 7 + 3) % 10];
            var c = words[(i * 13 + 5) % 10];
            result.Add($"{a} {b} {c} {a}");
        }

        return result;
    }

    static List<WordCount> Ranked(List<WordCount> items, Func<WordCount, WordCount, bool> before) {
        var sorted = new List<WordCount>(items);
        for (var i = 1; i < sorted.Count; i++) {
            for (var j = i; j > 0 && before(sorted[j], sorted[j - 1]); j--) {
                (sorted[j], sorted[j - 1]) = (sorted[j - 1], sorted[j]);
            }
        }

        return sorted;
    }

    public static string Words() {
        checked {
            var counts = new Dictionary<string, long>();
            var seen = new List<string>();
            for (var round = 0; round < 20; round++) {
                foreach (var line in Corpus(5000)) {
                    foreach (var word in line.Split(' ')) {
                        if (!counts.ContainsKey(word)) {
                            seen.Add(word);
                        }

                        counts[word] = counts.GetValueOrDefault(word) + 1;
                    }
                }
            }

            var items = new List<WordCount>();
            foreach (var word in seen) {
                items.Add(new WordCount(word, counts.GetValueOrDefault(word)));
            }

            return string.Join('\n', Ranked(items, (a, b) => a.Count > b.Count).Select(item => $"{item.Word} {item.Count}"));
        }
    }
}
