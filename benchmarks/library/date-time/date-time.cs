// Port of date-time.sb.
[Benchmark("library/date-time")]
static class LibraryDateTime {
    public static string Run() {
        checked {
            long total = 0;
            for (long i = 0; i < 200000; i++) {
                var moment = DateTimeOffset.FromUnixTimeSeconds(946684800 + i * 7919);
                var text = moment.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                var back = DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind);
                total += back.ToUnixTimeSeconds() % 1000 + back.Year + back.Month * 31 + back.Day + back.DayOfYear;
            }

            return total.ToString();
        }
    }
}
