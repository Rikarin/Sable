using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Sable.CodeGen.Tests;

/// <summary>
///     CI runs this project in shards, each the test classes
///     <c>tools/ci/codegen-shard.sh</c> gives it: every class with tests must
///     be in exactly one, or it would run twice or not at all.
/// </summary>
public sealed class ShardTests {
    static string Script => Path.Combine(Harnesses.Root, "tools", "ci", "codegen-shard.sh");

    static string Run(params string[] arguments) {
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
        start.ArgumentList.Add(Script);
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
        return output.Result;
    }

    /// <summary>The classes of this assembly xunit runs tests of, as a filter names them: an inner class after a '+'.</summary>
    static IEnumerable<string> TestClasses() =>
        typeof(ShardTests).Assembly.GetTypes()
            .Where(static t => t.IsClass && !t.IsAbstract)
            .Where(static t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Any(static m => m.IsDefined(typeof(FactAttribute), true)))
            .Select(static t => t.FullName![(t.Namespace!.Length + 1)..]);

    [PosixFact("the script is run with bash")]
    public void EveryTestClassIsInExactlyOneShard() {
        var count = int.Parse(Run("--count").Trim());
        var shards = Enumerable.Range(1, count)
            .Select(i => Run("--classes", i.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal))
            .ToList();
        var problems = TestClasses()
            .Select(c => (Class: c, In: shards.Count(s => s.Contains(c))))
            .Where(static c => c.In != 1)
            .Select(static c => c.In == 0 ? $"{c.Class} is in no shard: declare it 'public sealed class' at the top level of its file" : $"{c.Class} is in {c.In} shards")
            .ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [PosixFact("the script is run with bash")]
    public void TheWorkflowRunsEveryShard() {
        var count = int.Parse(Run("--count").Trim());
        var workflow = File.ReadAllText(Path.Combine(Harnesses.Root, ".github", "workflows", "ci.yml"));
        var matrix = Regex.Match(workflow, @"^\s*shard: \[([0-9, ]*)\]", RegexOptions.Multiline);
        Assert.True(matrix.Success, "the codegen job's matrix has no 'shard: [...]'");
        Assert.Equal(Enumerable.Range(1, count), matrix.Groups[1].Value.Split(',').Select(static s => int.Parse(s.Trim())));
    }
}
