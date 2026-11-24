// Port of time-zone.sb.
[Benchmark("library/time-zone")]
static class LibraryTimeZone {
    public static string Run() {
        checked {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            long total = 0;
            for (long i = 0; i < 500000; i++) {
                var moment = DateTimeOffset.FromUnixTimeSeconds(946684800 + i * 1261);
                var there = TimeZoneInfo.ConvertTime(moment, zone);
                total += there.Hour;
                if (zone.IsDaylightSavingTime(there)) { total++; }
            }

            return total.ToString();
        }
    }
}
