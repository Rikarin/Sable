using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.Driver.Packages;

/// <summary>A package <c>sable install</c> put in place: its version, and the binaries it installed.</summary>
public sealed record InstalledPackage(string Name, SemVersion Version, IReadOnlyList<string> Binaries);

/// <summary>
///     <c>sable install</c>: a registry package's binaries, built with the
///     release profile and copied to <c>$SABLE_HOME/bin</c>, as
///     <c>cargo install</c> does. The package is built in a directory of its
///     own, which is removed afterwards, so the cache keeps only sources.
///     What is installed is recorded in <c>$SABLE_HOME/installed.json</c>.
/// </summary>
public static class Installer {
    public static string BinDirectory(PackageEnvironment environment) => Path.Combine(environment.Home, "bin");

    static string RecordPath(PackageEnvironment environment) => Path.Combine(environment.Home, "installed.json");

    /// <summary>Installs <paramref name="specification" /> (<c>name</c> or <c>name@requirement</c>) from <paramref name="registry" />.</summary>
    /// <param name="bin">Only this binary; by default, all of them.</param>
    /// <param name="force">Replace binaries of the same names.</param>
    public static InstalledPackage Install(PackageEnvironment environment, IRegistry registry, string specification, string? bin, bool force) {
        var at = specification.IndexOf('@', 1);
        var name = at < 0 ? specification : specification[..at];
        VersionReq? requirement = null;
        if (at >= 0) {
            requirement = VersionReq.TryParse(specification[(at + 1)..], out var parsed, out var error) ? parsed : throw new PackageException($"'{specification[(at + 1)..]}': {error}");
        }

        if (!registry.IsAvailable) {
            throw new PackageException($"cannot install {name}: {registry.WhyUnavailable}");
        }

        var candidates = registry.Entries(name).Where(e => !e.Yanked && (requirement?.Matches(e.Version) ?? !e.Version.IsPrerelease)).ToList();
        if (candidates.Count == 0) {
            throw new PackageException(registry.Entries(name).Count == 0
                ? $"the registry has no package called '{name}'"
                : $"no version of {name} matches '{requirement?.Text ?? "a release"}'");
        }

        var entry = candidates.MaxBy(static e => e.Version)!;
        environment.Status.WriteLine($"  Installing {name} v{entry.Version}");
        var files = Registry.Unpack(registry.Download(entry));

        var work = Directory.CreateTempSubdirectory("sable-install-");
        try {
            var source = Path.Combine(work.FullName, LocalPackage.BinaryName(name));
            foreach (var (path, bytes) in files) {
                var file = Path.Combine(source, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }

            var workspace = Workspace.Load(source);
            var member = workspace.Members[0];
            if (member.Bins.Count == 0) {
                throw new PackageException($"{name} v{entry.Version} has no binaries to install; it is a library");
            }

            // Its dependencies come from the registry it came from.
            var built = PackageBuilder.Build(workspace, environment with { RegistryDirectory = registry.Location }, new(Release: true, Package: member.Name, Bin: bin))
                ?? throw new PackageException($"could not build {name} v{entry.Version}");

            var target = BinDirectory(environment);
            Directory.CreateDirectory(target);
            var record = Load(environment);
            foreach (var binary in built) {
                var destination = Path.Combine(target, Path.GetFileName(binary.Path));
                var owner = record.FirstOrDefault(p => p.Binaries.Contains(Path.GetFileName(binary.Path)));
                if (File.Exists(destination) && !force && owner?.Name != name) {
                    throw new PackageException($"{destination} exists already{(owner is null ? "" : $", installed by {owner.Name}")}; pass --force to replace it");
                }
            }

            var installed = new List<string>();
            foreach (var binary in built) {
                var destination = Path.Combine(target, Path.GetFileName(binary.Path));
                File.Copy(binary.Path, destination, overwrite: true);
                installed.Add(Path.GetFileName(binary.Path));
                environment.Status.WriteLine($"   Installed {destination}");
            }

            var package = new InstalledPackage(name, entry.Version, installed);
            Save(environment, [..record.Where(p => p.Name != name), package]);
            var searched = System.Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!searched.Split(Path.PathSeparator).Any(p => Path.TrimEndingDirectorySeparator(p) == Path.TrimEndingDirectorySeparator(target))) {
                environment.Error.WriteLine($"note: {target} is not on PATH; add it to run what was installed by name");
            }

            return package;
        } finally {
            work.Delete(true);
        }
    }

    /// <summary>Removes what <see cref="Install" /> installed for <paramref name="name" />.</summary>
    public static InstalledPackage Uninstall(PackageEnvironment environment, string name) {
        var record = Load(environment);
        var package = record.FirstOrDefault(p => p.Name == name) ?? throw new PackageException($"{name} is not installed");
        foreach (var binary in package.Binaries) {
            var path = Path.Combine(BinDirectory(environment), binary);
            if (File.Exists(path)) {
                File.Delete(path);
            }

            environment.Status.WriteLine($"    Removing {path}");
        }

        Save(environment, [..record.Where(p => p.Name != name)]);
        return package;
    }

    /// <summary>What is installed, by name.</summary>
    public static IReadOnlyList<InstalledPackage> Load(PackageEnvironment environment) {
        var path = RecordPath(environment);
        if (!File.Exists(path)) {
            return [];
        }

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return [
            ..root.Select(static p => new InstalledPackage(
                p.Key,
                SemVersion.Parse(p.Value!["version"]!.GetValue<string>()),
                [..p.Value["binaries"]!.AsArray().Select(static b => b!.GetValue<string>())])),
        ];
    }

    static void Save(PackageEnvironment environment, IReadOnlyList<InstalledPackage> packages) {
        var root = new JsonObject(packages.OrderBy(static p => p.Name, StringComparer.Ordinal).Select(static p => KeyValuePair.Create(p.Name, (JsonNode?)new JsonObject {
            ["version"] = p.Version.ToString(),
            ["binaries"] = new JsonArray([..p.Binaries.Select(static b => (JsonNode)JsonValue.Create(b)!)]),
        })));
        Directory.CreateDirectory(environment.Home);
        File.WriteAllText(RecordPath(environment), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
