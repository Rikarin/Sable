// What System.Numerics makes of the inputs, as the table
// tests/lib/Sable.Math.Geometry/DotNetReference.sb, which DotNetTests.sb
// compares Sable.Math.Geometry with:
//
//     dotnet run tools/conformance/geometry/reference.cs > tests/lib/Sable.Math.Geometry/DotNetReference.sb
//
// Each result is there for each of the sets of 24 inputs, in order.

using System.Globalization;
using System.Numerics;
using System.Text;

const int Sets = 6;
const int Inputs = 24;
var random = new Random(41);
var names = new List<string>();
var table = new Dictionary<string, List<float>>();

void Put(string name, params float[] values) {
    if (!table.TryGetValue(name, out var all)) {
        table[name] = all = [];
        names.Add(name);
    }

    all.AddRange(values);
}

void Put2(string name, Vector2 v) => Put(name, v.X, v.Y);
void Put3(string name, Vector3 v) => Put(name, v.X, v.Y, v.Z);
void Put4(string name, Vector4 v) => Put(name, v.X, v.Y, v.Z, v.W);
void PutQ(string name, Quaternion q) => Put(name, q.X, q.Y, q.Z, q.W);
void PutP(string name, Plane p) => Put(name, p.Normal.X, p.Normal.Y, p.Normal.Z, p.D);
void Put32(string name, Matrix3x2 m) => Put(name, m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);
void PutM(string name, Matrix4x4 m) => Put(name, m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

for (var set = 0; set < Sets; set++) {
    var p = new float[Inputs];
    for (var i = 0; i < Inputs; i++) {
        p[i] = MathF.Round((float)(random.NextDouble() * 20 - 10), 3);
    }

    Put("inputs", p);
    var a2 = new Vector2(p[0], p[1]);
    var b2 = new Vector2(p[2], p[3]);
    var a = new Vector3(p[0], p[1], p[2]);
    var b = new Vector3(p[3], p[4], p[5]);
    var c = new Vector3(p[6], p[7], p[8]);
    var a4 = new Vector4(p[0], p[1], p[2], p[3]);
    var b4 = new Vector4(p[4], p[5], p[6], p[7]);
    var t = MathF.Abs(p[9]) / 10;
    var angle = p[10] / 4;
    var near = MathF.Abs(p[11]) + 0.1f;
    var far = near + MathF.Abs(p[12]) * 10 + 1;
    var width = MathF.Abs(p[13]) + 1;
    var height = MathF.Abs(p[14]) + 1;
    var scale = new Vector3(MathF.Abs(p[15]) + 0.5f, MathF.Abs(p[16]) + 0.5f, MathF.Abs(p[17]) + 0.5f);
    var unitA = Vector3.Normalize(a);
    var unitC = Vector3.Normalize(c);

    Put2("vector2.add", a2 + b2);
    Put2("vector2.subtract", a2 - b2);
    Put2("vector2.multiply", a2 * b2);
    Put2("vector2.divide", a2 / b2);
    Put2("vector2.divideBy", a2 / p[4]);
    Put("vector2.numbers", Vector2.Dot(a2, b2), a2.Length(), Vector2.Distance(a2, b2), Vector2.DistanceSquared(a2, b2));
    Put2("vector2.normalized", Vector2.Normalize(a2));
    Put2("vector2.lerp", Vector2.Lerp(a2, b2, t));
    Put2("vector2.min", Vector2.Min(a2, b2));
    Put2("vector2.max", Vector2.Max(a2, b2));
    Put2("vector2.clamped", Vector2.Clamp(a2, new Vector2(-3), new Vector2(3)));
    Put2("vector2.reflected", Vector2.Reflect(a2, Vector2.Normalize(b2)));
    Put2("vector2.squareRoot", Vector2.SquareRoot(Vector2.Abs(a2)));

    Put3("vector3.add", a + b);
    Put3("vector3.subtract", a - b);
    Put3("vector3.multiply", a * b);
    Put3("vector3.divide", a / b);
    Put3("vector3.divideBy", a / p[9]);
    Put("vector3.numbers", Vector3.Dot(a, b), a.Length(), Vector3.Distance(a, b), Vector3.DistanceSquared(a, b), Vector3.Sum(a));
    Put3("vector3.cross", Vector3.Cross(a, b));
    Put3("vector3.normalized", unitA);
    Put3("vector3.lerp", Vector3.Lerp(a, b, t));
    Put3("vector3.min", Vector3.Min(a, b));
    Put3("vector3.max", Vector3.Max(a, b));
    Put3("vector3.clamped", Vector3.Clamp(a, new Vector3(-3), new Vector3(3)));
    Put3("vector3.reflected", Vector3.Reflect(a, unitC));
    Put3("vector3.squareRoot", Vector3.SquareRoot(Vector3.Abs(a)));
    Put3("vector3.truncated", Vector3.Truncate(a));
    Put3("vector3.rounded", Vector3.Round(a));

    Put4("vector4.add", a4 + b4);
    Put4("vector4.subtract", a4 - b4);
    Put4("vector4.multiply", a4 * b4);
    Put4("vector4.divide", a4 / b4);
    Put("vector4.numbers", Vector4.Dot(a4, b4), a4.Length(), Vector4.Distance(a4, b4), Vector4.DistanceSquared(a4, b4));
    Put4("vector4.normalized", Vector4.Normalize(a4));
    Put4("vector4.lerp", Vector4.Lerp(a4, b4, t));
    Put4("vector4.min", Vector4.Min(a4, b4));
    Put4("vector4.max", Vector4.Max(a4, b4));
    Put4("vector4.clamped", Vector4.Clamp(a4, new Vector4(-3), new Vector4(3)));

    var q = Quaternion.CreateFromAxisAngle(unitA, angle);
    var r = Quaternion.CreateFromYawPitchRoll(p[18] / 4, p[19] / 4, p[20] / 4);
    PutQ("quaternion.axisAngle", q);
    PutQ("quaternion.yawPitchRoll", r);
    PutQ("quaternion.concatenate", Quaternion.Concatenate(q, r));
    PutQ("quaternion.hamilton", q * r);
    PutQ("quaternion.slerp", Quaternion.Slerp(q, r, t));
    PutQ("quaternion.nlerp", Quaternion.Lerp(q, r, t));
    PutQ("quaternion.normalized", Quaternion.Normalize(new Quaternion(p[0], p[1], p[2], p[3])));
    PutQ("quaternion.inverted", Quaternion.Inverse(new Quaternion(p[0], p[1], p[2], p[3])));
    PutQ("quaternion.conjugate", Quaternion.Conjugate(r));
    Put("quaternion.numbers", Quaternion.Dot(q, r), new Quaternion(p[0], p[1], p[2], p[3]).Length());
    PutQ("quaternion.ofMatrix", Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateFromQuaternion(r)));
    Put3("quaternion.transform", Vector3.Transform(b, r));

    var turn = Matrix4x4.CreateFromQuaternion(q);
    var m = Matrix4x4.CreateScale(scale) * turn * Matrix4x4.CreateTranslation(b);
    var n = Matrix4x4.CreateScale(new Vector3(1, 2, 0.5f)) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(c);
    PutM("matrix.translation", Matrix4x4.CreateTranslation(a));
    PutM("matrix.scale", Matrix4x4.CreateScale(a));
    PutM("matrix.scaleAbout", Matrix4x4.CreateScale(a, b));
    PutM("matrix.rotationX", Matrix4x4.CreateRotationX(angle));
    PutM("matrix.rotationY", Matrix4x4.CreateRotationY(angle));
    PutM("matrix.rotationZ", Matrix4x4.CreateRotationZ(angle));
    PutM("matrix.axisAngle", Matrix4x4.CreateFromAxisAngle(unitA, angle));
    PutM("matrix.yawPitchRoll", Matrix4x4.CreateFromYawPitchRoll(p[18] / 4, p[19] / 4, p[20] / 4));
    PutM("matrix.rotation", turn);
    PutM("matrix.composed", m);
    PutM("matrix.multiply", m * n);
    PutM("matrix.add", m + n);
    PutM("matrix.subtract", m - n);
    PutM("matrix.times", m * 2.5f);
    PutM("matrix.lerp", Matrix4x4.Lerp(m, n, t));
    PutM("matrix.transposed", Matrix4x4.Transpose(m));
    Matrix4x4.Invert(m, out var inverse);
    PutM("matrix.inverted", inverse);
    Put("matrix.determinant", m.GetDeterminant());
    Matrix4x4.Decompose(m, out var partScale, out var partRotation, out var partTranslation);
    Put3("matrix.decomposedScale", partScale);
    PutQ("matrix.decomposedRotation", partRotation);
    Put3("matrix.decomposedTranslation", partTranslation);
    PutM("matrix.rotatedBy", Matrix4x4.Transform(m, r));
    Put3("matrix.transformPosition", Vector3.Transform(c, m));
    Put3("matrix.transformDirection", Vector3.TransformNormal(c, m));
    Put4("matrix.transformVector4", Vector4.Transform(a4, m));
    PutM("matrix.lookAt", Matrix4x4.CreateLookAt(a, b, Vector3.UnitY));
    PutM("matrix.lookTo", Matrix4x4.CreateLookTo(a, b, Vector3.UnitY));
    PutM("matrix.world", Matrix4x4.CreateWorld(a, b, Vector3.UnitY));
    PutM("matrix.fieldOfView", Matrix4x4.CreatePerspectiveFieldOfView(1 + t, width / height, near, far));
    PutM("matrix.fieldOfViewInfinite", Matrix4x4.CreatePerspectiveFieldOfView(1 + t, width / height, near, float.PositiveInfinity));
    PutM("matrix.perspective", Matrix4x4.CreatePerspective(width, height, near, far));
    PutM("matrix.perspectiveOffCenter", Matrix4x4.CreatePerspectiveOffCenter(-width, height, -height, width, near, far));
    PutM("matrix.orthographic", Matrix4x4.CreateOrthographic(width, height, near, far));
    PutM("matrix.orthographicOffCenter", Matrix4x4.CreateOrthographicOffCenter(-width, height, -height, width, near, far));
    PutM("matrix.viewport", Matrix4x4.CreateViewportLeftHanded(p[0], p[1], width, height, 0.25f, 0.75f));
    PutM("matrix.billboard", Matrix4x4.CreateBillboard(a, b, Vector3.UnitY, Vector3.UnitX));
    PutM("matrix.billboardNear", Matrix4x4.CreateBillboard(a, a, Vector3.UnitY, Vector3.UnitX));
    PutM("matrix.constrainedBillboard", Matrix4x4.CreateConstrainedBillboard(a, b, Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ));
    PutM("matrix.constrainedBillboardAlong", Matrix4x4.CreateConstrainedBillboard(a, a + Vector3.UnitY, Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ));
    PutM("matrix.constrainedBillboardAll", Matrix4x4.CreateConstrainedBillboard(a, a + Vector3.UnitY, Vector3.UnitY, Vector3.UnitX, Vector3.UnitY));
    PutM("matrix.constrainedBillboardZ", Matrix4x4.CreateConstrainedBillboard(a, a + Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitZ));
    var plane = new Plane(c, p[21]);
    PutM("matrix.reflection", Matrix4x4.CreateReflection(plane));
    PutM("matrix.shadow", Matrix4x4.CreateShadow(a, plane));

    PutP("plane.throughPoints", Plane.CreateFromVertices(a, b, c));
    PutP("plane.normalized", Plane.Normalize(plane));
    Put("plane.numbers", Plane.Dot(plane, a4), Plane.DotCoordinate(plane, a), Plane.DotNormal(plane, a));
    PutP("plane.transformed", Plane.Transform(Plane.Normalize(plane), turn * Matrix4x4.CreateTranslation(b)));
    PutP("plane.rotated", Plane.Transform(plane, r));

    var flat = Matrix3x2.CreateRotation(angle, a2) * Matrix3x2.CreateScale(scale.X, scale.Y) * Matrix3x2.CreateTranslation(b2);
    Put32("flat.rotation", Matrix3x2.CreateRotation(angle));
    Put32("flat.rotationAbout", Matrix3x2.CreateRotation(angle, a2));
    Put32("flat.scale", Matrix3x2.CreateScale(a2));
    Put32("flat.scaleAbout", Matrix3x2.CreateScale(a2, b2));
    Put32("flat.skew", Matrix3x2.CreateSkew(0.1f * p[4], 0.1f * p[5]));
    Put32("flat.skewAbout", Matrix3x2.CreateSkew(0.1f * p[4], 0.1f * p[5], a2));
    Put32("flat.multiply", flat);
    Matrix3x2.Invert(flat, out var flatInverse);
    Put32("flat.inverted", flatInverse);
    Put("flat.determinant", flat.GetDeterminant());
    Put32("flat.lerp", Matrix3x2.Lerp(flat, Matrix3x2.Identity, t));
    Put2("flat.transformPosition", Vector2.Transform(b2, flat));
    Put2("flat.transformDirection", Vector2.TransformNormal(b2, flat));
}

var text = new StringBuilder();
text.Append("module DotNetReference\n\n");
text.Append($"// What .NET {Environment.Version.Major}'s System.Numerics gives for the inputs, made by\n");
text.Append("// tools/conformance/geometry/reference.cs: not to be edited.\n\n");
text.Append("public fn dotNetReference() -> Dictionary<String, F32[>..]> {\n    var table = Dictionary<String, F32[>..]>()\n");
foreach (var name in names) {
    text.Append($"    table[\"{name}\"] = [");
    text.Append(string.Join(", ", table[name].Select(x => {
        var s = x.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('E') ? x.ToString("F45", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') : s;
    })));
    text.Append("]\n");
}

text.Append("    return table\n}\n");
Console.Write(text);
