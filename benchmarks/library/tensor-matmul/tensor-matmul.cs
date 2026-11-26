using System.Numerics.Tensors;

// Port of tensor-matmul.sb. System.Numerics.Tensors 11.0's Tensor<T> has no
// matrix product (its Multiply is elementwise, its Dot of all the elements),
// so the product is the one written over its TensorPrimitives: each row of C
// the rows of B times A's elements, added in by MultiplyAdd. The broadcast is
// the same loops a Tensor's Add and Multiply make, over spans; broadcast-tensor
// is the Tensor<T> itself, its Add broadcasting and its Multiply in place.
[Benchmark("library/tensor-matmul", "matmul")]
[Benchmark("library/tensor-matmul", "broadcast")]
[Benchmark("library/tensor-matmul", "broadcast-tensor")]
static class LibraryTensorMatmul {
    public static Func<string> Setup(string name) =>
        name switch {
            "matmul" => Product(512),
            "broadcast" => Broadcast(),
            _ => BroadcastTensor(),
        };

    static Func<string> BroadcastTensor() {
        var elements = new float[256 * 256 * 16];
        for (var i = 0; i < 256; i++) {
            for (var j = 0; j < 256; j++) {
                for (var k = 0; k < 16; k++) {
                    elements[(i * 256 + j) * 16 + k] = (i + j * 3 + k * 5) % 13;
                }
            }
        }

        var biases = new float[16];
        for (var k = 0; k < 16; k++) {
            biases[k] = k / 4f;
        }

        var t = Tensor.Create(elements, [256, 256, 16]);
        var bias = Tensor.Create(biases, [16]);
        return () => {
            var scaled = Tensor.Add<float>(t, bias);
            Tensor.Multiply<float>(scaled, 0.5f, scaled);
            var flat = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref scaled.GetPinnableReference(), 256 * 256 * 16);
            var sums = new float[256 * 256];
            for (var r = 0; r < 256 * 256; r++) {
                var total = 0f;
                for (var k = 0; k < 16; k++) {
                    total += flat[r * 16 + k];
                }

                sums[r] = total;
            }

            return $"{(long)(sums[256 * 256 - 1] * 4)}";
        };
    }

    static Func<string> Product(int size) {
        var x = Matrix(size, 1);
        var y = Matrix(size, 2);
        return () => {
            var c = Multiply(x, y, size);
            double total = 0;
            for (var i = 0; i < size; i++) {
                total += c[i * size + i];
            }

            return $"{total}";
        };
    }

    static Func<string> Broadcast() {
        var t = new float[256 * 256 * 16];
        for (var i = 0; i < 256; i++) {
            for (var j = 0; j < 256; j++) {
                for (var k = 0; k < 16; k++) {
                    t[(i * 256 + j) * 16 + k] = (i + j * 3 + k * 5) % 13;
                }
            }
        }

        var bias = new float[16];
        for (var k = 0; k < 16; k++) {
            bias[k] = k / 4f;
        }

        return () => {
            var scaled = new float[t.Length];
            for (var r = 0; r < 256 * 256; r++) {
                var row = new Span<float>(scaled, r * 16, 16);
                TensorPrimitives.Add(new ReadOnlySpan<float>(t, r * 16, 16), bias, row);
                TensorPrimitives.Multiply(row, 0.5f, row);
            }

            var sums = new float[256 * 256];
            for (var r = 0; r < 256 * 256; r++) {
                var total = 0f;
                for (var k = 0; k < 16; k++) {
                    total += scaled[r * 16 + k];
                }

                sums[r] = total;
            }

            return $"{(long)(sums[256 * 256 - 1] * 4)}";
        };
    }

    static float[] Matrix(int size, int seed) {
        var m = new float[size * size];
        for (var i = 0; i < size; i++) {
            for (var j = 0; j < size; j++) {
                m[i * size + j] = (i * 7 + j * 3 + seed) % 11 / 8f - 0.5f;
            }
        }

        return m;
    }

    static float[] Multiply(float[] a, float[] b, int n) {
        var c = new float[n * n];
        for (var i = 0; i < n; i++) {
            var row = new Span<float>(c, i * n, n);
            for (var k = 0; k < n; k++) {
                TensorPrimitives.MultiplyAdd(new ReadOnlySpan<float>(b, k * n, n), a[i * n + k], row, row);
            }
        }

        return c;
    }
}
