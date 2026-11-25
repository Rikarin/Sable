using System.Numerics;

// Port of matrix-invert.sb, with System.Numerics.
[Benchmark("library/matrix-invert")]
static class LibraryMatrixInvert {
    static float Total(Matrix4x4[] matrices) {
        var sum = 0f;
        for (var i = 0; i < matrices.Length; i++) {
            if (Matrix4x4.Invert(matrices[i], out var inverse)) {
                sum += inverse.M41;
            }

            if (Matrix4x4.Decompose(matrices[i], out var scale, out _, out var translation)) {
                sum += scale.Y + translation.X;
            }
        }

        return sum;
    }

    public static string Run() {
        checked {
            const int count = 256;
            var matrices = new Matrix4x4[count];
            for (var i = 0; i < count; i++) {
                matrices[i] = Matrix4x4.CreateScale(1, 2, 3) * Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, i % 3)), i % 60 * 0.1f));
                matrices[i].Translation = new Vector3(i % 5, 1, -1);
            }

            var sum = 0.0;
            for (var round = 0; round < 4000; round++) {
                matrices[0].M41 = round % 16;
                sum += Total(matrices);
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}", sum / 1024000);
        }
    }
}
