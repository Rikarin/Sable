// Port of diagnostics-disabled.sb: an ActivitySource no listener listens to,
// and Meter instruments no MeterListener enables.
using System.Diagnostics;
using System.Diagnostics.Metrics;

[Benchmark("library/diagnostics-disabled")]
static class LibraryDiagnosticsDisabled {
    static readonly ActivitySource Source = new("bench");
    static readonly Meter Meter = new("bench");
    static readonly Counter<long> Requests = Meter.CreateCounter<long>("requests");
    static readonly Histogram<double> Durations = Meter.CreateHistogram<double>("durations");
    static readonly object[] Routes = [0, 1, 2, 3];

    public static string Run() {
        long total = 0;
        for (var i = 0; i < 1_000_000; i++) {
            using (var activity = Source.StartActivity("operation")) {
                activity?.SetTag("i", i);
                total += activity is not null ? 0 : i & 7;
            }

            // The tag's value boxed beforehand, as Sable's attributes are
            // not made at all with nothing listening.
            Requests.Add(1, new KeyValuePair<string, object?>("route", Routes[i & 3]));
            Durations.Record(i & 1023);
        }

        return total.ToString();
    }
}
