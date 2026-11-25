using System.Numerics;

// Port of frustum-cull.sb: System.Numerics has no frustum, so one of its planes and vectors.
[Benchmark("library/frustum-cull")]
static class LibraryFrustumCull {
    const float Slack = 4.7683716e-7f;

    static bool Behind(in Plane plane, Vector3 center, Vector3 extent) {
        var normal = Vector3.Abs(plane.Normal);
        var reach = Vector3.Dot(extent, normal);
        var margin = Slack * (reach + Vector3.Dot(Vector3.Abs(center), normal) + MathF.Abs(plane.D));
        return Plane.DotCoordinate(plane, center) < -reach - margin;
    }

    static bool Behind(in Plane plane, Vector3 center, float radius) {
        var margin = Slack * (radius + Vector3.Dot(Vector3.Abs(center), Vector3.Abs(plane.Normal)) + MathF.Abs(plane.D));
        return Plane.DotCoordinate(plane, center) < -radius - margin;
    }

    static int Seen(Plane[] planes, (Vector3 Center, float Radius)[] spheres, (Vector3 Minimum, Vector3 Maximum)[] boxes) {
        var count = 0;
        for (var i = 0; i < spheres.Length; i++) {
            var sphere = spheres[i];
            var inside = true;
            foreach (ref readonly var plane in planes.AsSpan()) {
                if (Behind(plane, sphere.Center, sphere.Radius)) {
                    inside = false;
                    break;
                }
            }

            if (!inside) {
                continue;
            }

            count++;
            var box = boxes[i];
            foreach (ref readonly var plane in planes.AsSpan()) {
                if (Behind(plane, (box.Minimum + box.Maximum) * 0.5f, (box.Maximum - box.Minimum) * 0.5f)) {
                    inside = false;
                    break;
                }
            }

            if (inside) {
                count++;
            }
        }

        return count;
    }

    public static string Run() {
        checked {
            var spheres = new (Vector3 Center, float Radius)[4096];
            var boxes = new (Vector3 Minimum, Vector3 Maximum)[4096];
            for (var i = 0; i < 4096; i++) {
                var center = new Vector3(i % 61 - 30, i % 37 - 18, i % 101 - 90);
                spheres[i] = (center, 1.75f);
                boxes[i] = (center - Vector3.One, center + Vector3.One);
            }

            var projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 1.5f, 0.5f, 80);
            var planes = new Plane[6];
            var total = 0;
            for (var round = 0; round < 1000; round++) {
                var m = Matrix4x4.Transpose(Matrix4x4.CreateLookAt(new Vector3(round % 8, 0, 5), new Vector3(0, 0, -40), Vector3.UnitY) * projection);
                var x = new Vector4(m.M11, m.M12, m.M13, m.M14);
                var y = new Vector4(m.M21, m.M22, m.M23, m.M24);
                var z = new Vector4(m.M31, m.M32, m.M33, m.M34);
                var w = new Vector4(m.M41, m.M42, m.M43, m.M44);
                planes[0] = Plane.Normalize(new Plane(z));
                planes[1] = Plane.Normalize(new Plane(w - z));
                planes[2] = Plane.Normalize(new Plane(w + x));
                planes[3] = Plane.Normalize(new Plane(w - x));
                planes[4] = Plane.Normalize(new Plane(w - y));
                planes[5] = Plane.Normalize(new Plane(w + y));
                total += Seen(planes, spheres, boxes);
            }

            return $"{total / 100000}";
        }
    }
}
