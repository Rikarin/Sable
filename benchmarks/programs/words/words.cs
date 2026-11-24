// Port of words.sb.
[Benchmark("programs/words")]
static class ProgramWords {
    record struct WordCount(string Word, long Count);

    static List<string> Corpus(int lines) {
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa"];
        var result = new List<string>();
        for (var i = 0; i < lines; i++) {
            var a = words[i % 10];
            var b = words[(i * 7 + 3) % 10];
            var c = words[(i * 13 + 5) % 10];
            result.Add($"{a} {b} {c} {a}");
        }

        return result;
    }

    static List<WordCount> Ranked(List<WordCount> items, Func<WordCount, WordCount, bool> before) {
        var sorted = new List<WordCount>(items);
        for (var i = 1; i < sorted.Count; i++) {
            for (var j = i; j > 0 && before(sorted[j], sorted[j - 1]); j--) {
                (sorted[j], sorted[j - 1]) = (sorted[j - 1], sorted[j]);
            }
        }

        return sorted;
    }

    public static string Run() {
        checked {
            var counts = new Dictionary<string, long>();
            var seen = new List<string>();
            for (var round = 0; round < 20; round++) {
                foreach (var line in Corpus(5000)) {
                    foreach (var word in line.Split(' ')) {
                        if (!counts.ContainsKey(word)) {
                            seen.Add(word);
                        }

                        counts[word] = counts.GetValueOrDefault(word) + 1;
                    }
                }
            }

            var items = new List<WordCount>();
            foreach (var word in seen) {
                items.Add(new WordCount(word, counts.GetValueOrDefault(word)));
            }

            return string.Join('\n', Ranked(items, (a, b) => a.Count > b.Count).Select(item => $"{item.Word} {item.Count}"));
        }
    }
}
