using System.Text;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Random registries for the PubGrub resolver, and random text for the
///     version parsers it rests on.
/// </summary>
/// <remarks>
///     <para>
///         A registry is a handful of packages with a few versions each —
///         pre-releases, build metadata and yanked versions among them — whose
///         versions depend on one another (the root and themselves included,
///         so there are cycles), with requirements drawn from every operator,
///         partial and wildcard versions, comma-joined ranges and text that
///         does not parse; and sometimes a lock file preferring versions that
///         are yanked or do not exist. The resolver must end, within a time
///         limit, with an answer or a <see cref="ResolutionException" />. An
///         answer must satisfy every requirement of every version chosen, choose
///         only what the root needs, and choose nothing yanked the lock did not
///         hold. A failure must be right: where no pre-release complicates the
///         rules, every assignment is tried, and none may work.
///     </para>
///     <para>
///         Settings are the parser fuzzer's: <c>SABLE_FUZZ_SEED</c> moves the
///         seed and <c>SABLE_FUZZ_SCALE</c> multiplies the count; a failure
///         prints the registry.
///     </para>
/// </remarks>
public sealed class ResolverFuzzTests(Xunit.Abstractions.ITestOutputHelper output) {
    static int? Setting(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    static Random RandomFor(int fixedSeed) => new(Setting("SABLE_FUZZ_SEED") is { } night ? unchecked(fixedSeed * 31 + night) : fixedSeed);

    static int Iterations(int usual) => usual * Math.Max(1, Setting("SABLE_FUZZ_SCALE") ?? 1);

    static readonly string[] Versions = [
        "0.0.1", "0.1.0", "0.1.1", "0.2.0", "1.0.0", "1.0.1", "1.1.0", "1.2.3", "2.0.0", "3.0.0",
        "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-beta.2", "2.0.0-rc.1", "1.0.0+build.7",
    ];

    static readonly string[] Numbers = ["0", "1", "2", "3", "10", "00", "01", "x", "*", "X", "", "a", "-1", "9223372036854775807", "99999999999999999999"];

    static readonly string[] Operators = ["", "^", "~", "=", ">=", ">", "<", "<=", "==", "=>", "~>", " "];

    /// <summary>A requirement, mostly well formed and sometimes not.</summary>
    static string Requirement(Random random, bool prereleases = true) {
        var builder = new StringBuilder();
        for (var comparators = random.Next(5) == 0 ? 2 : 1; comparators > 0; comparators--) {
            if (builder.Length > 0) {
                builder.Append(random.Next(8) == 0 ? "," : ", ");
            }

            var op = Operators[random.Next(random.Next(4) == 0 ? Operators.Length : 8)];
            builder.Append(op);
            if (random.Next(10) == 0) {
                builder.Append(Numbers[random.Next(Numbers.Length)]);
                continue;
            }

            var parts = random.Next(1, random.Next(6) == 0 ? 5 : 4);
            for (var i = 0; i < parts; i++) {
                builder.Append(i == 0 ? "" : ".").Append(random.Next(5) == 0 ? Numbers[random.Next(Numbers.Length)] : random.Next(3).ToString());
            }

            if (prereleases && random.Next(6) == 0) {
                builder.Append(random.Next(2) == 0 ? "-alpha" : "-rc.1");
            }
        }

        return builder.ToString();
    }

    /// <summary>A registry in memory, with what a lock file holds deciding which yanked versions may still be chosen, as for a real one.</summary>
    sealed class Registry : IPackageSource {
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
    }

    static readonly SemVersion RootVersion = SemVersion.Parse("1.0.0");

    static Registry Generate(Random random) {
        var registry = new Registry();

        // Half the registries without pre-releases, whose failures can be checked by trying everything.
        var prereleases = random.Next(2) == 0;
        var names = Enumerable.Range(0, random.Next(1, 6)).Select(static i => $"p{i}").ToList();
        var targets = names.Append("root").Append("ghost").ToList();

        List<(string, VersionReq)> DependenciesOf(string self) {
            var dependencies = new List<(string, VersionReq)>();
            for (var n = random.Next(4); n > 0; n--) {
                var target = targets[random.Next(targets.Count)];
                if (dependencies.Any(d => d.Item1 == target) || (target == self && random.Next(3) > 0)) {
                    continue;
                }

                // What does not parse is the manifest's error, before any resolving; drop it.
                if (VersionReq.TryParse(Requirement(random, prereleases), out var requirement, out _)) {
                    dependencies.Add((target, requirement));
                }
            }

            return dependencies;
        }

        registry.Packages["root"] = [(RootVersion, false, DependenciesOf("root"))];
        foreach (var name in names) {
            var versions = Versions.Where(v => prereleases || !v.Contains('-')).OrderBy(_ => random.Next()).Take(random.Next(1, 5)).Select(SemVersion.Parse)
                .DistinctBy(static v => v.ToString().Split('+')[0]).ToList();
            registry.Packages[name] = [..versions.Select(v => (v, random.Next(6) == 0, DependenciesOf(name)))];
            if (random.Next(4) == 0) {
                registry.Locked[name] = random.Next(5) == 0 ? SemVersion.Parse("9.9.9") : versions[random.Next(versions.Count)];
            }
        }

        return registry;
    }

    [Fact]
    public void RandomRegistriesResolveCorrectlyOrFailTruthfully() {
        var random = RandomFor(20261122);
        var (solved, failed, provenImpossible) = (0, 0, 0);
        for (var iteration = 0; iteration < Iterations(300); iteration++) {
            var registry = Generate(random);
            IReadOnlyDictionary<string, SemVersion>? solution = null;
            Exception? failure = null;
            var thread = new Thread(() => {
                try {
                    solution = Resolver.Resolve(registry, "root", RootVersion, registry.Locked);
                } catch (Exception e) {
                    failure = e;
                }
            }, 8 * 1024 * 1024) { IsBackground = true };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), $"the resolver did not finish:\n{registry}");

            if (failure is not null) {
                Assert.True(failure is ResolutionException, $"the resolver threw {failure}\n{Reproduce(registry)}");
                Assert.False(string.IsNullOrWhiteSpace(failure.Message), $"an empty explanation\n{Reproduce(registry)}");
                failed++;
                if (!registry.HasPrereleases) {
                    if (BruteForce(registry) is { } exists) {
                        Assert.Fail($"the resolver found no solution, but {Describe(exists)} is one\n{failure.Message}\n{Reproduce(registry)}");
                    }

                    provenImpossible++;
                }

                continue;
            }

            Assert.NotNull(solution);
            if (Violation(registry, solution!) is { } violation) {
                Assert.Fail($"{violation}; chose {Describe(solution!)}\n{Reproduce(registry)}");
            }

            solved++;
        }

        output.WriteLine($"{solved} solved and checked; {failed} failed, {provenImpossible} of them shown to have no solution by trying every one");
    }

    static string Reproduce(Registry registry) =>
        $"SABLE_FUZZ_SEED={Environment.GetEnvironmentVariable("SABLE_FUZZ_SEED") ?? "(unset)"}; the registry:\n{registry}";

    static string Describe(IReadOnlyDictionary<string, SemVersion> solution) =>
        string.Join(", ", solution.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => $"{p.Key} {p.Value}"));

    /// <summary>What is wrong with a solution, if anything.</summary>
    static string? Violation(Registry registry, IReadOnlyDictionary<string, SemVersion> solution) {
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

    /// <summary>Every assignment of a version, or none, to each package: one that satisfies every requirement, if there is one.</summary>
    static Dictionary<string, SemVersion>? BruteForce(Registry registry) {
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

    // ---- The version parsers and the set algebra ----------------------------------------------------

    static readonly char[] Alphabet = "0123456789.-+^~=<>*xX, abz\t".ToCharArray();

    static string Noise(Random random) {
        var builder = new StringBuilder();
        for (var n = random.Next(14); n > 0; n--) {
            builder.Append(Alphabet[random.Next(Alphabet.Length)]);
        }

        return builder.ToString();
    }

    [Fact]
    public void RandomTextNeverThrowsFromTheVersionParsers() {
        var random = RandomFor(20261123);
        for (var iteration = 0; iteration < Iterations(5000); iteration++) {
            var text = random.Next(2) == 0 ? Noise(random) : Requirement(random);
            Exception? thrown = null;
            try {
                if (SemVersion.TryParse(text, out var version, out _)) {
                    // What a version writes reads back as the same version.
                    Assert.True(SemVersion.TryParse(version.ToString(), out var again, out _) && again == version && again.ToString() == version.ToString(), $"'{text}' does not round-trip");
                }

                VersionReq.TryParse(text, out _, out _);
            } catch (Exception e) when (e is not Xunit.Sdk.XunitException) {
                thrown = e;
            }

            Assert.True(thrown is null, $"'{text}' threw {thrown}");
        }
    }

    /// <summary>
    ///     A requirement naming a whole version (<c>^1.2.3</c>, <c>~1.2.3</c>,
    ///     <c>=1.2.3</c>, <c>&gt;=1.2.3</c>, <c>&lt;=1.2.3</c>) admits that
    ///     version, whatever its numbers.
    /// </summary>
    [Fact]
    public void ARequirementNamingAVersionAdmitsIt() {
        var random = RandomFor(20261124);
        string[] operators = ["", "^", "~", "=", ">=", "<="];
        string[] numbers = ["0", "1", "2", "7", "10", "4294967296", "9223372036854775806", "9223372036854775807"];
        for (var iteration = 0; iteration < Iterations(2000); iteration++) {
            var version = $"{numbers[random.Next(numbers.Length)]}.{numbers[random.Next(numbers.Length)]}.{numbers[random.Next(numbers.Length)]}";
            var text = operators[random.Next(operators.Length)] + version;
            if (!VersionReq.TryParse(text, out var requirement, out _)) {
                continue;
            }

            Assert.True(requirement.Set.Contains(SemVersion.Parse(version)), $"'{text}' does not admit {version}: it is {requirement.Set}");
        }
    }

    [Fact]
    public void TheSetAlgebraAgreesWithMembership() {
        var random = RandomFor(20261125);
        var samples = new List<SemVersion>();
        foreach (var major in new[] { 0, 1, 2, 3 }) {
            foreach (var minor in new[] { 0, 1, 2 }) {
                foreach (var patch in new[] { 0, 1, 3 }) {
                    samples.Add(new(major, minor, patch));
                    samples.Add(new(major, minor, patch, ["0"]));
                    samples.Add(new(major, minor, patch, ["alpha"]));
                    samples.Add(new(major, minor, patch, ["rc", "1"]));
                }
            }
        }

        VersionSet RandomSet() {
            for (;;) {
                if (VersionReq.TryParse(Requirement(random), out var requirement, out _)) {
                    var set = requirement.Set;
                    return random.Next(4) switch {
                        0 => set.Complement(),
                        1 when VersionReq.TryParse(Requirement(random), out var other, out _) => set.Union(other.Set),
                        _ => set,
                    };
                }
            }
        }

        for (var iteration = 0; iteration < Iterations(1000); iteration++) {
            var a = RandomSet();
            var b = RandomSet();
            var union = a.Union(b);
            var intersection = a.Intersect(b);
            var complement = a.Complement();
            var difference = a.Difference(b);
            foreach (var v in samples) {
                var (inA, inB) = (a.Contains(v), b.Contains(v));
                var context = $"{v} in A = {a}, B = {b}";
                Assert.True(union.Contains(v) == (inA || inB), $"union: {context}");
                Assert.True(intersection.Contains(v) == (inA && inB), $"intersection: {context}");
                Assert.True(complement.Contains(v) == !inA, $"complement: {context}");
                Assert.True(difference.Contains(v) == (inA && !inB), $"difference: {context}");
                Assert.True(!a.IsSubsetOf(b) || !inA || inB, $"subset: {context}");
                Assert.True(!a.IsDisjointFrom(b) || !(inA && inB), $"disjoint: {context}");
            }

            Assert.True(a.Complement().Complement().Equals(a), $"double complement of {a}");
        }
    }
}
