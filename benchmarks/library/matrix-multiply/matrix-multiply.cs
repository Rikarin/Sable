using System.Numerics;

// Port of matrix-multiply.sb, with System.Numerics.
[Benchmark("library/matrix-multiply")]
static class LibraryMatrixMultiply {
    static void Update(Matrix4x4[] world, Matrix4x4[] local, int[] parents) {
        world[0] = local[0];
        for (var i = 1; i < local.Length; i++) {
            world[i] = local[i] * world[parents[i]];
        }
    }

    public static string Run() {
        checked {
            const int count = 256;
            var local = new Matrix4x4[count];
            var parents = new int[count];
            for (var i = 0; i < count; i++) {
                local[i] = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, i % 3)), i * 0.1f));
                local[i].Translation = new Vector3(i % 5, 1, -1);
                parents[i] = i / 2;
            }

            var world = (Matrix4x4[])local.Clone();
            var sum = 0.0;
            for (var round = 0; round < 40000; round++) {
                local[0].M41 = round % 16;
                Update(world, local, parents);
                sum += world[255].M41;
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", sum / 40000);
        }
    }
}
