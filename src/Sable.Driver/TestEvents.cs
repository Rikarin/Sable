using System.Text;
using System.Text.Json;

namespace Sable.Driver;

/// <summary>
///     A run as JSON, an object a line as each thing happens (<c>sable test
///     --json -</c>): what an editor follows a run by, and what <c>--json
///     &lt;path&gt;</c> writes of it once it is done.
/// </summary>
/// <remarks>
///     <c>{"event":"run","tests":[…]}</c>; for each test <c>{"event":"started","name"}</c>
///     and <c>{"event":"finished","name","outcome","duration",…}</c>, the outcome
///     <c>passed</c>, <c>failed</c> or <c>skipped</c>, the duration in seconds, with
///     what it said (<c>message</c>, <c>output</c>, <c>errors</c>), why it was skipped
///     (<c>reason</c>), its tries when there were several, and the files it kept;
///     and last <c>{"event":"ended","passed","failed","skipped","filteredOut","duration"}</c>.
///     A reader passes over what it does not know.
/// </remarks>
internal sealed class TestEvents(TextWriter output) {
    public void Begin(IReadOnlyList<TestRunner.TestEntry> tests) =>
        Write("run", json => {
            json.WriteNumber("count", tests.Count);
        });

    public void Started(TestRunner.TestEntry test) => Write("started", json => json.WriteString("name", test.Name));

    public void Finished(TestRunner.Outcome outcome) =>
        Write("finished", json => {
            var failed = TestRunner.Verdict(outcome) is not null;
            json.WriteString("name", outcome.Test.Name);
            json.WriteString("location", outcome.Test.Location);
            json.WriteString("outcome", outcome.Skipped is not null ? "skipped" : failed ? "failed" : "passed");
            json.WriteNumber("duration", Math.Round(outcome.Took.TotalSeconds, 6));
            if (outcome.Skipped is { } reason) {
                json.WriteString("reason", reason);
            }

            if (outcome.Attempts > 1) {
                json.WriteNumber("attempts", outcome.Attempts);
            }

            if (failed) {
                json.WriteString("message", TestRunner.Details(outcome).Trim('\n'));
            }

            if (outcome.Output.Length > 0) {
                json.WriteString("output", outcome.Output);
            }

            if (TestRunner.Said(outcome) is { Length: > 0 } errors) {
                json.WriteString("errors", errors);
            }

            if (outcome.Attachments.Count > 0) {
                json.WriteStartArray("attachments");
                foreach (var attachment in outcome.Attachments) {
                    json.WriteStringValue(attachment);
                }

                json.WriteEndArray();
            }
        });

    public void End(int passed, int failed, int skipped, int filteredOut, TimeSpan took) =>
        Write("ended", json => {
            json.WriteNumber("passed", passed);
            json.WriteNumber("failed", failed);
            json.WriteNumber("skipped", skipped);
            json.WriteNumber("filteredOut", filteredOut);
            json.WriteNumber("duration", Math.Round(took.TotalSeconds, 6));
        });

    void Write(string name, Action<Utf8JsonWriter> fields) {
        using var stream = new MemoryStream();
        // Text as it is: the lines are read by programs, not by a browser.
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
            json.WriteStartObject();
            json.WriteString("event", name);
            fields(json);
            json.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
