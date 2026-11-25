using System.Text.RegularExpressions;

static class RegexSample {
    // The regex benchmarks' text, as library/regex/regex.sb makes it: about a
    // megabyte of words chosen by xorshift, with names and e-mail addresses among
    // them, in lines of a dozen words or so, a few of the words not ASCII.
    public static string Text() {
        string[] words = [
            "the", "of", "and", "to", "in", "was", "he", "it", "that", "his", "with", "had", "as", "for",
            "you", "at", "which", "on", "but", "be", "not", "by", "from", "my", "have", "there", "one",
            "been", "all", "naïve", "café", "so", "door", "room", "said", "upon", "little", "man", "matter", "case",
        ];
        string[] names = ["Sherlock Holmes", "Holmes", "Watson", "Irene Adler", "Lestrade", "Mrs. Hudson"];
        string[] emails = ["sherlock.holmes@baker-street.co.uk", "j.watson+notes@example.com", "irene@adler.org"];
        var state = 0x9E3779B97F4A7C15UL;

        int Next(int below) {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return (int)(state % (ulong)below);
        }

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 150000; i++) {
            var choice = Next(100);
            if (choice < 3) {
                builder.Append(names[Next(names.Length)]);
            } else if (choice < 4) {
                builder.Append(emails[Next(emails.Length)]);
            } else {
                builder.Append(words[Next(words.Length)]);
            }

            builder.Append(Next(12) == 0 ? ".\n" : " ");
        }

        return builder.ToString();
    }
}
