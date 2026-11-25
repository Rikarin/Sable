using System.Numerics;

// Port of vector-particles.sb, with System.Numerics.
[Benchmark("library/vector-particles")]
static class LibraryVectorParticles {
    struct Particle {
        public Vector3 Position;
        public Vector3 Velocity;
    }

    static void Step(Particle[] particles, float dt) {
        var gravity = new Vector3(0, -9.8f, 0);
        var center = new Vector3(0, 10, 0);
        for (var i = 0; i < particles.Length; i++) {
            var p = particles[i];
            var toward = center - p.Position;
            var pull = toward * (4 / (1 + toward.LengthSquared()));
            p.Velocity = Vector3.Clamp(p.Velocity + (gravity + pull) * dt, new Vector3(-50), new Vector3(50));
            p.Position += p.Velocity * dt;
            if (p.Position.Y < 0) {
                p.Position.Y = -p.Position.Y;
                p.Velocity = Vector3.Reflect(p.Velocity, Vector3.UnitY) * 0.9f;
            }

            particles[i] = p;
        }
    }

    public static string Run() {
        checked {
            var particles = new Particle[4096];
            for (var i = 0; i < 4096; i++) {
                particles[i] = new Particle { Position = new Vector3(i % 64 - 32, i % 10, i / 64 - 32) };
            }

            for (var round = 0; round < 2000; round++) {
                Step(particles, 0.002f);
            }

            var sum = 0.0;
            foreach (var p in particles) {
                sum += p.Position.Length();
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}", sum / 4096);
        }
    }
}
