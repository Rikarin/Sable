using System.IO.Compression;
using System.Text;

static class CompressionSample {
    static readonly string[] Words = [
        "the", "of", "and", "to", "in", "a", "is", "that", "for", "it", "as", "was",
        "with", "be", "by", "on", "not", "he", "his", "but", "at", "are", "this", "have",
        "from", "or", "had", "an", "they", "which", "one", "you", "were", "all", "her", "she",
        "there", "would", "their", "we", "him", "been", "has", "when", "who", "will", "no", "more",
        "if", "out", "so", "up", "said", "what", "its", "about", "than", "into", "them", "can",
        "only", "other", "time", "new", "some", "could", "these", "two", "may", "first", "then", "do",
        "any", "like", "my", "now", "over", "such", "our", "man", "me", "even", "most", "made",
        "after", "also", "did", "many", "before", "must", "well", "back", "through", "years", "where", "much",
        "your", "way", "down", "should", "because", "long", "each", "just", "state", "those", "people", "too",
        "mr", "how", "little", "good", "world", "make", "very", "year", "still", "see", "own", "work",
        "men", "day", "get", "here", "old", "life", "both", "between", "being", "under", "three", "never",
        "know", "same", "last", "another", "while", "us", "off", "might", "great", "go", "come", "since",
        "against", "right", "came", "take", "used", "himself", "few", "house", "american", "use", "place", "during",
        "high", "without", "again", "home",
    ];

    // Two megabytes of words the Sable side makes the same way.
    public static byte[] Text() {
        using var bytes = new MemoryStream();
        var state = 1UL;
        while (bytes.Length < 2_000_000) {
            state = unchecked(state * 6364136223846793005 + 1442695040888963407);
            var random = (long)(state >> 33);
            var first = random % 160;
            var second = random / 160 % 160;
            bytes.Write(Encoding.UTF8.GetBytes(Words[first < second ? first : second]));
            var kind = random / 40000 % 16;
            bytes.Write(kind == 0 ? ".\n"u8 : kind == 1 ? Encoding.UTF8.GetBytes($" {random % 10007} ") : " "u8);
        }

        return bytes.ToArray();
    }

    public static byte[] Compressed(byte[] text, Func<Stream, Stream> compressing) {
        using var packed = new MemoryStream();
        using (var stream = compressing(packed)) {
            stream.Write(text);
        }

        return packed.ToArray();
    }

    // Read into memory of its length, which .NET's one-shot readers do as well.
    public static long Decompressed(Stream stream, byte[] into) {
        long length = 0;
        using (stream) {
            while (length < into.Length) {
                var read = stream.Read(into.AsSpan((int)length));
                if (read == 0) {
                    break;
                }

                length += read;
            }
        }

        return length + into[length / 2];
    }
}
