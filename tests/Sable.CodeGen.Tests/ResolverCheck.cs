using System.Text;
using System.Text.RegularExpressions;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A registry in memory, with what a lock file holds deciding which yanked
///     versions may still be chosen, as for a real one.
/// </summary>
/// <remarks>
///     It writes itself as text that <see cref="Parse" /> reads back, one
///     version a line, so that a failing registry is its own reproducer:
///     <code>
///     root 1.0.0: p0 '^1.0', p1 '>=0.1, &lt;2'
///     p0 1.0.0 (yanked): root '1'
///     locked: p0 1.0.0
///     </code>
///     The coverage-guided fuzzer (tools/fuzz/Sable.Fuzz, which compiles this
///     file in) mutates that text.
/// </remarks>
public sealed partial class MemoryRegistry : IPackageSource {
    public readonly Dictionary<string, List<(SemVersion Version, bool Yanked, List<(string Package, VersionReq Requirement)> Dependencies)>> Packages = new(StringComparer.Ordinal);
    public readonly Dictionary<string, SemVersion> Locked = new(StringComparer.Ordinal);

    public IReadOnlyList<SemVersion> Versions(string package) =>
        Packages.TryGetValue(package, out var versions)
            ? [..versions.Where(v => !v.Yanked || Locked.GetValueOrDefault(package) == v.Version).Select(static v => v.Version)]
            : [];

    public IReadOnlyList<(string Package, VersionReq Requirement)> Dependencies(string package, SemVersion version) =>
        Packages[package].First(v => v.Version == version).Dependencies;

    public bool HasPrereleases => Packages.Values.Any(static vs => vs.Any(static v => v.Version.IsPrerelease || v.Dependencies.Any(static d => d.Requirement.MentionsPrerelease)));

    public override string ToString() {
        var builder = new StringBuilder();
        foreach (var (package, versions) in Packages) {
            foreach (var (version, yanked, dependencies) in versions) {
                builder.Append($"  {package} {version}{(yanked ? " (yanked)" : "")}: ")
                    .AppendJoin(", ", dependencies.Select(static d => $"{d.Package} '{d.Requirement}'"))
                    .AppendLine();
            }
        }

        foreach (var (package, version) in Locked) {
            builder.AppendLine($"  locked: {package} {version}");
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(\S+)\s+'([^']*)'")]
    private static partial Regex Dependency();

    /// <summary>
    ///     A registry from its text, as <see cref="ToString" /> writes it, with
    ///     whatever does not read — a line, a version, a requirement — left
    ///     out, and at most <paramref name="packages" /> packages of at most
    ///     <paramref name="versions" /> versions each. The root is always at
    ///     <see cref="ResolverCheck.RootVersion" />, with the dependencies of its
    ///     first line.
    /// </summary>
    public static MemoryRegistry Parse(string text, int packages = 8, int versions = 6) {
        var registry = new MemoryRegistry();
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith("locked:", StringComparison.Ordinal)) {
                var locked = line[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (locked.Length == 2 && SemVersion.TryParse(locked[1], out var at, out _)) {
                    registry.Locked[locked[0]] = at;
                }

                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0) {
                continue;
            }

            var head = line[..colon].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (head.Length is < 2 or > 3 || !SemVersion.TryParse(head[1], out var version, out _)) {
                continue;
            }

            var name = head[0];
            if (name == "root") {
                if (registry.Packages.ContainsKey("root")) {
                    continue;
                }

                version = ResolverCheck.RootVersion;
            }

            if (!registry.Packages.TryGetValue(name, out var list)) {
                if (registry.Packages.Count >= packages) {
                    continue;
                }

                registry.Packages[name] = list = [];
            }

            if (list.Count >= versions || list.Any(v => v.Version == version)) {
                continue;
            }

            var dependencies = new List<(string, VersionReq)>();
            foreach (Match match in Dependency().Matches(line[(colon + 1)..])) {
                var target = match.Groups[1].Value.TrimStart(',');
                if (target.Length > 0 && dependencies.All(d => d.Item1 != target) && VersionReq.TryParse(match.Groups[2].Value, out var requirement, out _)) {
                    dependencies.Add((target, requirement));
                }
            }

            list.Add((version, name != "root" && head.Length == 3 && head[2] == "(yanked)", dependencies));
        }

        if (!registry.Packages.ContainsKey("root")) {
            registry.Packages["root"] = [(ResolverCheck.RootVersion, false, [])];
        }

        return registry;
    }
}

/// <summary>What the resolver owes a registry: an answer that holds, or a failure that is true.</summary>
public static class ResolverCheck {
    public static readonly SemVersion RootVersion = SemVersion.Parse("1.0.0");

    public static string Describe(IReadOnlyDictionary<string, SemVersion> solution) =>
        string.Join(", ", solution.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => $"{p.Key} {p.Value}"));

    /// <summary>What is wrong with a solution, if anything.</summary>
    public static string? Violation(MemoryRegistry registry, IReadOnlyDictionary<string, SemVersion> solution) {
        if (solution.GetValueOrDefault("root") != RootVersion) {
            return "the root is not chosen at its version";
        }

        foreach (var (package, version) in solution) {
            if (!registry.Versions(package).Contains(version)) {
                return $"{package} {version} is not a version to choose";
            }

            foreach (var (dependency, requirement) in registry.Dependencies(package, version)) {
                if (!solution.TryGetValue(dependency, out var chosen)) {
                    return $"{package} {version} needs {dependency} '{requirement}', and none is chosen";
                }

                if (!requirement.Set.Contains(chosen)) {
                    return $"{package} {version} needs {dependency} '{requirement}', and {chosen} is chosen";
                }
            }

            // A pre-release is chosen only where a requirement names one, or the lock holds it.
            if (version.IsPrerelease && registry.Locked.GetValueOrDefault(package) != version
                && !registry.Packages.Values.SelectMany(static vs => vs).SelectMany(static v => v.Dependencies).Any(d => d.Package == package && d.Requirement.MentionsPrerelease)) {
                return $"the pre-release {package} {version} is chosen, and nothing asks for pre-releases of it";
            }
        }

        // Only what the root needs, through what is chosen.
        var needed = new HashSet<string>(StringComparer.Ordinal) { "root" };
        var pending = new Stack<string>(["root"]);
        while (pending.TryPop(out var package)) {
            foreach (var (dependency, _) in registry.Dependencies(package, solution[package])) {
                if (needed.Add(dependency)) {
                    pending.Push(dependency);
                }
            }
        }

        return solution.Keys.FirstOrDefault(p => !needed.Contains(p)) is { } extra ? $"{extra} is chosen, and nothing chosen needs it" : null;
    }

    /// <summary>How many assignments <see cref="BruteForce" /> would try at most.</summary>
    public static long Assignments(MemoryRegistry registry) =>
        registry.Packages.Keys.Where(static p => p != "root").Aggregate(1L, (n, p) => n > int.MaxValue ? n : n * (registry.Versions(p).Count + 1));

    /// <summary>Every assignment of a version, or none, to each package: one that satisfies every requirement, if there is one.</summary>
    public static Dictionary<string, SemVersion>? BruteForce(MemoryRegistry registry) {
        var packages = registry.Packages.Keys.Where(static p => p != "root").ToList();
        var choice = new Dictionary<string, SemVersion>(StringComparer.Ordinal) { ["root"] = RootVersion };

        bool Works() =>
            choice.All(chosen => registry.Dependencies(chosen.Key, chosen.Value)
                .All(d => choice.TryGetValue(d.Package, out var version) && d.Requirement.Set.Contains(version)));

        bool Search(int index) {
            if (index == packages.Count) {
                return Works();
            }

            var package = packages[index];
            if (Search(index + 1)) {
                return true;
            }

            foreach (var version in registry.Versions(package)) {
                choice[package] = version;
                if (Search(index + 1)) {
                    return true;
                }
            }

            choice.Remove(package);
            return false;
        }

        return Search(0) ? choice : null;
    }
}
