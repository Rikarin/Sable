// Port of string.sb.
[Benchmark("library/string")]
static class LibraryString {
    public static string Run() {
        checked {
            string[] names = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
            var lines = new List<string>();
            for (var i = 0; i < 300000; i++) {
                lines.Add($"{names[i % 8]} {i} {names[i * 3 % 8]} end");
            }

            long characters = 0;
            long matches = 0;
            long words = 0;
            foreach (var line in lines) {
                foreach (var word in line.Split(' ')) {
                    words++;
                    characters += word.Length;
                    if (word == "gamma") {
                        matches++;
                    }
                }
            }

            long joinedLength = 0;
            for (var i = 0; i < lines.Count; i += 16) {
                var block = "";
                for (var j = 0; j < 16 && i + j < lines.Count; j++) {
                    block = block + lines[i + j] + "\n";
                }

                joinedLength += block.Length;
            }

            long sliced = 0;
            foreach (var line in lines) {
                if (line[..4] == "alph") {
                    sliced++;
                }
            }

            return $"{words} {characters} {matches} {joinedLength} {sliced}";
        }
    }
}
