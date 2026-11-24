// Sum, dot, cosine similarity, add, multiply and softmax over 4096 floats,
// System.Numerics.Tensors' TensorPrimitives: twenty thousand rounds (two
// thousand of softmax), one element changed a round.
[Benchmark("library/tensor-primitives")]
static class LibraryTensorPrimitives {
    public static string Run() {
        checked {
            const int n = 4096;
            var x = new float[n];
            var y = new float[n];
            for (var i = 0; i < n; i++) {
                x[i] = i % 7 - 3;
                y[i] = i % 5 - 2;
            }

            var sums = (float[])y.Clone();
            var products = (float[])y.Clone();
            var probabilities = (float[])y.Clone();
            var total = 0.0;
            var cosine = 0.0;
            for (var round = 0; round < 20000; round++) {
                x[round % n] = round % 7 - 3;
                total += (double)System.Numerics.Tensors.TensorPrimitives.Sum<float>(x) + System.Numerics.Tensors.TensorPrimitives.Dot<float>(x, y);
                cosine += System.Numerics.Tensors.TensorPrimitives.CosineSimilarity<float>(x, y);
                System.Numerics.Tensors.TensorPrimitives.Add<float>(x, y, sums);
                System.Numerics.Tensors.TensorPrimitives.Multiply<float>(sums, y, products);
                total += products[round % n];
            }

            var spread = 0.0;
            for (var round = 0; round < 2000; round++) {
                y[round % n] = round % 5 - 2;
                System.Numerics.Tensors.TensorPrimitives.SoftMax<float>(y, probabilities);
                spread += probabilities[round % n];
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return string.Format(inv, "{0:F1} {1:F3} {2:F1} {3:F4}", total, cosine, (double)System.Numerics.Tensors.TensorPrimitives.Sum<float>(products), spread);
        }
    }
}
