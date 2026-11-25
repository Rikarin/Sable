using System.Globalization;

// Port of shapes.sb.
[Benchmark("programs/shapes")]
static class ProgramsShapes {
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

    public static string Run() {
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
}
