using System.Numerics;

// Port of transform-points.sb, with System.Numerics, which has no transform of a span.
[Benchmark("library/transform-points")]
static class LibraryTransformPoints {
    static void Transform(Vector3[] points, in Matrix4x4 matrix, Vector3[] moved) {
        for (var i = 0; i < points.Length; i++) {
            moved[i] = Vector3.Transform(points[i], matrix);
        }
    }

    static float Lengths(Vector3[] points, Quaternion turn) {
        var sum = 0f;
        for (var i = 0; i < points.Length; i++) {
            sum += Vector3.Dot(Vector3.Normalize(Vector3.Transform(points[i] + Vector3.UnitY, turn)), Vector3.One);
        }

        return sum;
    }

    public static string Run() {
        checked {
            const int count = 4096;
            var points = new Vector3[count];
            for (var i = 0; i < count; i++) {
                points[i] = new Vector3(i % 17, i % 13, i % 11);
            }

            var moved = (Vector3[])points.Clone();
            var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 1, 0)), 0.5f);
            var sum = 0.0;
            for (var round = 0; round < 4000; round++) {
                var matrix = Matrix4x4.CreateScale(1, 2, 1) * Matrix4x4.CreateFromQuaternion(turn);
                matrix.Translation = new Vector3(round % 8, 0, 1);
                Transform(points, matrix, moved);
                sum += moved[round % count].X;
            }

            for (var round = 0; round < 1000; round++) {
                points[0].X = round % 8;
                sum += (double)Lengths(points, turn) / 4096;
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", sum / 5000);
        }
    }
}
