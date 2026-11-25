// Port of small-vec.sb: .NET has no small vector, so each list is an
// [InlineArray(8)] buffer and a count, kept on the stack — what a SmallVec,
// an ArrayVec and an InlineArray are within their room.
using System.Runtime.CompilerServices;

[Benchmark("library/small-vec")]
static class LibrarySmallVec {
    [InlineArray(8)]
    struct Eight {
        long first;
    }

    public static string Run() {
        long total = 0;
        for (long i = 0; i < 1000000; i++) {
            var n = (int)(i % 8) + 1;
            var small = new Eight();
            var smallCount = 0;
            var bounded = new Eight();
            var boundedCount = 0;
            var fixedArray = new Eight();
            for (var k = 0; k < n; k++) {
                small[smallCount++] = i + k;
                bounded[boundedCount++] = i - k;
                fixedArray[k] = i * k;
            }

            // An insert at the front, within the room: the rest moved up one.
            Span<long> smallSpan = small;
            if (n < 8) {
                smallSpan[..smallCount].CopyTo(smallSpan[1..]);
                smallSpan[0] = n;
                smallCount++;
            }

            Span<long> boundedSpan = bounded;
            boundedSpan[0] = boundedSpan[boundedCount - 1];
            boundedCount--;

            Span<long> fixedSpan = fixedArray;
            (fixedSpan[0], fixedSpan[n - 1]) = (fixedSpan[n - 1], fixedSpan[0]);
            for (var k = 0; k < smallCount; k++) {
                total = unchecked(total + smallSpan[k]);
            }

            for (var k = 0; k < boundedCount; k++) {
                total = unchecked(total + boundedSpan[k]);
            }

            if (fixedSpan.Contains(i)) {
                total = unchecked(total + 1);
            }

            total = unchecked(total + fixedSpan[0] + smallCount + boundedCount);
        }

        return total.ToString();
    }
}
