using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Math.Tensors, whose F32 and F64 matrix products read and write
///     their arrays by address a vector at a time: the interpreter, which
///     checks every access, gives what native code gives. The library's own
///     tests (tests/lib/Sable.Math.Tensors) hold it to NumPy.
/// </summary>
[Native]
public sealed class TensorTests {
    [Fact]
    public void AMatrixProductIsTheSameEveryWay() =>
        Assert.Equal(
            """
            [5, 33] 0.0 0.0 -3.5
            [5, 33] 0.0 0.0 -3.5
            [2, 5, 3] 25 110
            [3] [1, 7, 13]

            """,
            AssertAgree("""
                module T

                import Sable.Math.Tensors

                @main
                fn main() {
                    let a = Tensor<F32>(shape: [5, 7]) { F32(($0[0] * 3 + $0[1]) % 5) - 2 }
                    let b = Tensor<F32>(shape: [7, 33]) { F32(($0[0] + $0[1] * 2) % 7) / 2 - 1 }
                    let c = Tensor.matmul(a, b)
                    print("\(c.shape) \(c[0, 0]) \(c.sum()) \(c[4, 32])")
                    let d = Tensor.matmul(a.map { F64($0) }, b.map { F64($0) })
                    print("\(d.shape) \(d[0, 0]) \(d.sum()) \(d[4, 32])")
                    let x = Tensor<Int>(shape: [2, 1, 3]) { $0[0] * 3 + $0[2] }
                    let y = Tensor<Int>(shape: [5, 1]) { $0[0] * 10 }
                    let z = x + y
                    print("\(z.shape) \(z[1, 2, 2]) \(z.sum(axis: 1)[0, 2])")
                    let m = Tensor<Int>(shape: [3, 2]) { $0[0] * 2 + $0[1] }
                    let v = Tensor<Int>(shape: [2], elements: [2, 1])
                    print("\(Tensor.matmul(m, v).shape) \(Tensor.matmul(m, v).toArray())")
                }
                """).Stdout);
}
