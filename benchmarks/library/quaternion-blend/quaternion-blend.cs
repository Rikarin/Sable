using System.Numerics;

// Port of quaternion-blend.sb, with System.Numerics.
[Benchmark("library/quaternion-blend")]
static class LibraryQuaternionBlend {
    static void Blend(Quaternion[] from, Quaternion[] to, float amount, Quaternion[] result) {
        for (var i = 0; i < from.Length; i++) {
            result[i] = Quaternion.Lerp(from[i], to[i], amount);
        }

        for (var i = 1; i < from.Length; i++) {
            result[i] = Quaternion.Concatenate(result[i], result[i / 2]);
        }
    }

    static void Steady(Quaternion[] from, Quaternion[] to, float amount, Quaternion[] result) {
        for (var i = 0; i < from.Length; i++) {
            result[i] = Quaternion.Slerp(from[i], to[i], amount);
        }
    }

    public static string Run() {
        checked {
            const int count = 128;
            var from = new Quaternion[count];
            var to = new Quaternion[count];
            for (var i = 0; i < count; i++) {
                from[i] = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, i % 3)), i % 20 * 0.1f);
                to[i] = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(i % 3, 2, 1)), i % 30 * 0.1f);
            }

            var result = (Quaternion[])from.Clone();
            var sum = 0.0;
            for (var round = 0; round < 10000; round++) {
                var amount = round % 16 / 16f;
                Blend(from, to, amount, result);
                sum += Math.Abs(result[127].W);
                Steady(from, to, amount, result);
                sum += Math.Abs(result[127].W);
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", sum / 20000);
        }
    }
}
