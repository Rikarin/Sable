using System.Globalization;

// Port of nbody.sb.
[Benchmark("programs/nbody")]
static class ProgramNbody {
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

    public static string Run() {
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
}
