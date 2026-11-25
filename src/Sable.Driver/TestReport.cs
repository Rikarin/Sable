using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Sable.Driver;

/// <summary>What a run's results are written as, besides being printed.</summary>
internal enum TestReportFormat {
    /// <summary>JUnit's XML, as Ant wrote it and every CI reads it.</summary>
    Junit,

    /// <summary>Visual Studio's test results (TRX), as <c>dotnet test --logger trx</c> writes them.</summary>
    Trx,

    /// <summary>A JSON object a line, as <see cref="TestEvents" /> has them.</summary>
    Json,
}

/// <summary>A file of a run's results: <c>sable test --junit out.xml --trx out.trx</c>.</summary>
/// <remarks>
///     A test's class, in both, is the module (or type) it is declared in;
///     its name, the rest, a <c>@test(arguments:)</c> case's arguments among
///     it. JUnit has no place for a test's tags, so its <c>properties</c>
///     hold one each, as xUnit's and pytest's writers do.
/// </remarks>
internal sealed record TestReport(TestReportFormat Format, string Path) {
    /// <summary>Writes the results; a run whose file cannot be written has still run.</summary>
    public void Write(IReadOnlyList<TestRunner.Outcome> outcomes, DateTimeOffset begun, TimeSpan took) {
        try {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory) {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(Path);
            Write(stream, outcomes, begun, took);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"sable: cannot write '{Path}': {e.Message}");
        }
    }

    internal void Write(Stream stream, IReadOnlyList<TestRunner.Outcome> outcomes, DateTimeOffset begun, TimeSpan took) {
        if (Format == TestReportFormat.Json) {
            using var lines = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
            var events = new TestEvents(lines);
            events.Begin([..outcomes.Select(static o => o.Test)]);
            foreach (var outcome in outcomes) {
                events.Finished(outcome);
            }

            var failed = outcomes.Count(static o => TestRunner.Verdict(o) is not null);
            var skipped = outcomes.Count(static o => o.Skipped is not null);
            events.End(outcomes.Count - failed - skipped, failed, skipped, 0, took);
            return;
        }

        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), NewLineChars = "\n" });
        writer.WriteStartDocument();
        if (Format == TestReportFormat.Junit) {
            Junit(writer, outcomes, begun, took);
        } else {
            Trx(writer, outcomes, begun, took);
        }

        writer.WriteEndDocument();
    }

    static void Junit(XmlWriter writer, IReadOnlyList<TestRunner.Outcome> outcomes, DateTimeOffset begun, TimeSpan took) {
        writer.WriteStartElement("testsuites");
        Counts(writer, outcomes, took);
        foreach (var suite in outcomes.GroupBy(static o => Split(o.Test.Name).Class)) {
            var tests = suite.ToList();
            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", suite.Key);
            Counts(writer, tests, TimeSpan.FromTicks(tests.Sum(static t => t.Took.Ticks)));
            writer.WriteAttributeString("timestamp", begun.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var test in tests) {
                var (type, name) = Split(test.Test.Name);
                var (file, line) = Place(test.Test.Location);
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("classname", type);
                writer.WriteAttributeString("name", Clean(name));
                writer.WriteAttributeString("file", file);
                writer.WriteAttributeString("line", line);
                writer.WriteAttributeString("time", Seconds(test.Took));
                if (test.Test.Tags is { Count: > 0 } tags) {
                    writer.WriteStartElement("properties");
                    foreach (var tag in tags) {
                        writer.WriteStartElement("property");
                        writer.WriteAttributeString("name", "tag");
                        writer.WriteAttributeString("value", tag);
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }

                if (test.Skipped is { } reason) {
                    writer.WriteStartElement("skipped");
                    writer.WriteAttributeString("message", Clean(reason));
                    writer.WriteEndElement();
                }

                if (TestRunner.Verdict(test) is not null) {
                    writer.WriteStartElement("failure");
                    writer.WriteAttributeString("message", Message(test));
                    writer.WriteAttributeString("type", test.TimedOut ? "timeout" : "failure");
                    writer.WriteString(Clean(TestRunner.Details(test).Trim('\n')));
                    writer.WriteEndElement();
                }

                // An attachment is a line of its output, as Jenkins reads one.
                Output(writer, "system-out", test.Output + string.Concat(test.Attachments.Select(static a => $"[[ATTACHMENT|{System.IO.Path.GetFullPath(a)}]]\n")));
                Output(writer, "system-err", TestRunner.Said(test));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    static void Counts(XmlWriter writer, IReadOnlyList<TestRunner.Outcome> tests, TimeSpan took) {
        writer.WriteAttributeString("tests", tests.Count.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("failures", tests.Count(static t => TestRunner.Verdict(t) is not null).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("errors", "0");
        writer.WriteAttributeString("skipped", tests.Count(static t => t.Skipped is not null).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("time", Seconds(took));
    }

    static void Output(XmlWriter writer, string element, string text) {
        if (text.Length > 0) {
            writer.WriteElementString(element, Clean(text));
        }
    }

    /// <summary>The GUID Visual Studio knows a unit test by, as a type of test.</summary>
    const string UnitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b";

    /// <summary>The list tests are in that are in no other: Visual Studio's own.</summary>
    const string ResultsNotInAList = "8c84fa94-04c1-424b-9868-57a2d4851a1d";

    static void Trx(XmlWriter writer, IReadOnlyList<TestRunner.Outcome> outcomes, DateTimeOffset begun, TimeSpan took) {
        const string space = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        static string Time(DateTimeOffset at) => at.ToString("o", CultureInfo.InvariantCulture);
        var entries = outcomes.Select(static o => (Outcome: o, Test: Id("test:" + o.Test.Name), Execution: Guid.NewGuid().ToString())).ToList();

        writer.WriteStartElement("TestRun", space);
        writer.WriteAttributeString("id", Guid.NewGuid().ToString());
        writer.WriteAttributeString("name", string.Create(CultureInfo.InvariantCulture, $"sable test {begun:yyyy-MM-dd HH:mm:ss}"));

        writer.WriteStartElement("Times", space);
        writer.WriteAttributeString("creation", Time(begun));
        writer.WriteAttributeString("queuing", Time(begun));
        writer.WriteAttributeString("start", Time(begun));
        writer.WriteAttributeString("finish", Time(begun + took));
        writer.WriteEndElement();

        writer.WriteStartElement("Results", space);
        foreach (var (test, id, execution) in entries) {
            writer.WriteStartElement("UnitTestResult", space);
            writer.WriteAttributeString("executionId", execution);
            writer.WriteAttributeString("testId", id);
            writer.WriteAttributeString("testName", Clean(test.Test.Name));
            writer.WriteAttributeString("computerName", Environment.MachineName);
            writer.WriteAttributeString("duration", test.Took.ToString("c", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("testType", UnitTestType);
            writer.WriteAttributeString("outcome", test.Skipped is not null ? "NotExecuted" : TestRunner.Verdict(test) is null ? "Passed" : "Failed");
            writer.WriteAttributeString("testListId", ResultsNotInAList);
            var failed = TestRunner.Verdict(test) is not null;
            var errors = TestRunner.Said(test);
            if (failed || test.Output.Length > 0 || errors.Length > 0 || test.Skipped is not null) {
                writer.WriteStartElement("Output", space);
                if (test.Output.Length > 0) {
                    writer.WriteElementString("StdOut", space, Clean(test.Output));
                }

                if (errors.Length > 0) {
                    writer.WriteElementString("StdErr", space, Clean(errors));
                }

                if (test.Skipped is { } reason) {
                    writer.WriteStartElement("ErrorInfo", space);
                    writer.WriteElementString("Message", space, Clean(reason));
                    writer.WriteEndElement();
                }

                if (failed) {
                    writer.WriteStartElement("ErrorInfo", space);
                    writer.WriteElementString("Message", space, Clean(TestRunner.Details(test).Trim('\n')));
                    writer.WriteElementString("StackTrace", space, $"at {test.Test.Name} in {Place(test.Test.Location).File}:line {Place(test.Test.Location).Line}");
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            if (test.Attachments.Count > 0) {
                writer.WriteStartElement("ResultFiles", space);
                foreach (var attachment in test.Attachments) {
                    writer.WriteStartElement("ResultFile", space);
                    writer.WriteAttributeString("path", System.IO.Path.GetFullPath(attachment));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteStartElement("TestDefinitions", space);
        foreach (var (test, id, execution) in entries) {
            var (type, name) = Split(test.Test.Name);
            writer.WriteStartElement("UnitTest", space);
            writer.WriteAttributeString("name", Clean(test.Test.Name));
            writer.WriteAttributeString("id", id);
            if (test.Test.Tags is { Count: > 0 } tags) {
                writer.WriteStartElement("TestCategory", space);
                foreach (var tag in tags) {
                    writer.WriteStartElement("TestCategoryItem", space);
                    writer.WriteAttributeString("TestCategory", tag);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteStartElement("Execution", space);
            writer.WriteAttributeString("id", execution);
            writer.WriteEndElement();
            writer.WriteStartElement("TestMethod", space);
            writer.WriteAttributeString("codeBase", Place(test.Test.Location).File);
            writer.WriteAttributeString("adapterTypeName", "executor://sable/test");
            writer.WriteAttributeString("className", type);
            writer.WriteAttributeString("name", Clean(name));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteStartElement("TestEntries", space);
        foreach (var (_, id, execution) in entries) {
            writer.WriteStartElement("TestEntry", space);
            writer.WriteAttributeString("testId", id);
            writer.WriteAttributeString("executionId", execution);
            writer.WriteAttributeString("testListId", ResultsNotInAList);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteStartElement("TestLists", space);
        writer.WriteStartElement("TestList", space);
        writer.WriteAttributeString("name", "Results Not in a List");
        writer.WriteAttributeString("id", ResultsNotInAList);
        writer.WriteEndElement();
        writer.WriteEndElement();

        var failures = outcomes.Count(static o => TestRunner.Verdict(o) is not null);
        var skips = outcomes.Count(static o => o.Skipped is not null);
        writer.WriteStartElement("ResultSummary", space);
        writer.WriteAttributeString("outcome", failures == 0 ? "Completed" : "Failed");
        writer.WriteStartElement("Counters", space);
        foreach (var (name, count) in new[] {
                     ("total", outcomes.Count), ("executed", outcomes.Count - skips), ("passed", outcomes.Count - failures - skips), ("failed", failures),
                     ("error", 0), ("timeout", 0), ("aborted", 0), ("inconclusive", 0), ("passedButRunAborted", 0), ("notRunnable", 0),
                     ("notExecuted", skips), ("disconnected", 0), ("warning", 0), ("completed", 0), ("inProgress", 0), ("pending", 0),
                 }) {
            writer.WriteAttributeString(name, count.ToString(CultureInfo.InvariantCulture));
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>The test's module and types, and its own name with a case's arguments, whose dots are theirs.</summary>
    internal static (string Class, string Name) Split(string name) {
        var open = name.IndexOf('(', StringComparison.Ordinal);
        var dot = (open < 0 ? name : name[..open]).LastIndexOf('.');
        return dot < 0 ? ("", name) : (name[..dot], name[(dot + 1)..]);
    }

    static (string File, string Line) Place(string location) {
        var colon = location.LastIndexOf(':');
        return colon < 0 ? (location, "0") : (location[..colon], location[(colon + 1)..]);
    }

    /// <summary>The first line of what went wrong.</summary>
    static string Message(TestRunner.Outcome test) =>
        TestRunner.Verdict(test) is { Length: > 0 } why
            ? Clean(why)
            : Clean(test.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : "the test failed");

    static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>An id that is the same for the same test in every run, as TRX's readers match runs by.</summary>
    static string Id(string text) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16)).ToString();

    /// <summary>The text without what XML cannot hold: control characters, and halves of surrogate pairs.</summary>
    static string Clean(string text) {
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                clean.Append(text[i]).Append(text[++i]);
            } else {
                clean.Append(XmlConvert.IsXmlChar(text[i]) ? text[i] : '�');
            }
        }

        return clean.ToString();
    }
}
