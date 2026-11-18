using System.Security.Cryptography;
using System.Text;

namespace Sable.Driver.Packages;

/// <summary>
///     A resolution kept for the next build: when the workspace's manifests,
///     its lock file and the request are what they were, the graph is the one
///     resolution gave last time, and the resolver need not run again. Only a
///     graph of local packages (members and path dependencies) is kept; one
///     with registry packages, whose sources and index live outside the
///     workspace, is resolved every time.
/// </summary>
public sealed partial class PackageGraph {
    const string ResolutionVersion = "sable-resolution-1";

    /// <summary>Resolves as <see cref="Resolve" /> does, reusing the last resolution when nothing it read has changed.</summary>
    public static PackageGraph ResolveCached(Workspace workspace, PackageEnvironment environment, ResolveOptions options) {
        // Never with an update asked for: that is a request to resolve afresh.
        if (options.Update is not null) {
            return Resolve(workspace, environment, options);
        }

        var locals = workspace.LocalPackages();
        var key = ResolutionKey(workspace, locals, options);
        var path = Path.Combine(workspace.TargetDirectory, "resolution.txt");
        if (Restore(workspace, environment, locals, key, path) is { } restored) {
            return restored;
        }

        var graph = Resolve(workspace, environment, options);
        if (graph.Packages.Values.All(static p => p.IsLocal)) {
            Save(graph, ResolutionKey(workspace, workspace.LocalPackages(), options), path);
        }

        return graph;
    }

    /// <summary>
    ///     A hash of everything a resolution of local packages reads: every
    ///     local package's manifest and directory, the lock file, the request.
    /// </summary>
    static string ResolutionKey(Workspace workspace, IReadOnlyDictionary<string, LocalPackage> locals, ResolveOptions options) {
        var text = new StringBuilder(ResolutionVersion).Append('\n').Append(typeof(PackageGraph).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        text.Append(workspace.Root).Append('\n');
        text.Append(ManifestFiles.Text(workspace.Root)).Append('\n');
        text.Append(File.Exists(workspace.LockPath) ? File.ReadAllText(workspace.LockPath) : "<no lock>").Append('\n');
        foreach (var (name, package) in locals.OrderBy(static p => p.Key, StringComparer.Ordinal)) {
            text.Append(name).Append(' ').Append(package.Directory).Append('\n').Append(ManifestFiles.Text(package.Directory)).Append('\n');
        }

        var features = options.Features ?? FeatureRequest.Default;
        text.Append($"features={string.Join(",", features.Features)} none={features.NoDefaultFeatures} all={features.AllFeatures} locked={options.Locked}\n");
        text.Append($"packages={string.Join(",", options.Packages ?? [])}\n");
        text.Append($"target={options.Target?.Os ?? Sable.Syntax.ParseOptions.HostOs}/{options.Target?.Arch ?? Sable.Syntax.ParseOptions.HostArch}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    static void Save(PackageGraph graph, string key, string path) {
        var text = new StringBuilder(key).Append('\n');
        foreach (var package in graph.Packages.Values.OrderBy(static p => p.Name, StringComparer.Ordinal)) {
            text.Append(package.Name).Append('\t')
                .Append(string.Join(",", package.Features.Order(StringComparer.Ordinal))).Append('\t')
                .Append(string.Join(",", package.Dependencies)).Append('\t')
                .Append(string.Join(",", package.DevDependencies)).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, text.ToString());
        File.Move(temporary, path, overwrite: true);
    }

    static PackageGraph? Restore(Workspace workspace, PackageEnvironment environment, IReadOnlyDictionary<string, LocalPackage> locals, string key, string path) {
        if (!File.Exists(path)) {
            return null;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0] != key) {
            return null;
        }

        var packages = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1)) {
            var fields = line.Split('\t');
            if (fields.Length != 4 || !locals.TryGetValue(fields[0], out var local)) {
                return null;
            }

            static string[] List(string field) => field.Length == 0 ? [] : field.Split(',');
            packages[fields[0]] = new(local, true, null, List(fields[1]).ToHashSet(StringComparer.Ordinal), List(fields[2]), List(fields[3]));
        }

        var lockFile = LockFile.Load(workspace.LockPath);
        if (lockFile is null) {
            return null;
        }

        var registry = environment.RegistryFor(workspace);
        return new(workspace, packages, lockFile, new SourceStore(workspace, environment, registry, lockFile));
    }
}
