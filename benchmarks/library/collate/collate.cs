// Port of collate.sb.
[Benchmark("library/collate")]
static class LibraryCollate {
    public static string Run() {
        string[] letters = ["a", "b", "c", "e", "k", "l", "o", "r", "s", "t", "å", "ä", "ö"];
        var words = new List<string>();
        long x = 42;
        for (var i = 0; i < 5000; i++) {
            var word = "";
            for (var j = 0; j < 6; j++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                word += letters[(int)(x / 65536 % letters.Length)];
            }

            words.Add(word);
        }

        var swedish = new System.Globalization.CultureInfo("sv-SE").CompareInfo;
        var sorted = new List<string>(words);
        sorted.Sort((a, b) => swedish.Compare(a, b));
        var keyed = words.Select(w => (Key: swedish.GetSortKey(w), Word: w)).ToList();
        keyed.Sort((a, b) => System.Globalization.SortKey.Compare(a.Key, b.Key));
        return $"{sorted[0]} {sorted[^1]} {sorted[2500]} {keyed[2500].Word}";
    }
}
