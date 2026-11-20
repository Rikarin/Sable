using Sable.CodeGen;
using Sable.Driver.Build;
using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable cache</c>: what <c>$SABLE_HOME/cache</c> holds — the fetched
///     packages (<see cref="PackageCache" />) and the build cache's entries
///     (<see cref="BuildCache" />) — collecting what is no longer used, and
///     filling it ahead of a build.
/// </summary>
public sealed class CacheCommand : PackageCommand {
    public override string Name => "cache";
    public override string Summary => "list, prune, clean or warm the package and build cache";

    public override string Usage => """
        usage: sable cache list
               sable cache prune [--max-age <days>]
               sable cache clean
               sable cache warm [--target <triple>] [--profile dev|release] [--import <Module>]... [--into <dir>]

          list       the fetched packages and the build cache's entries — the runtime's and the
                     standard library's archives for each target and profile — with their sizes
                     and when a build last used them
          prune      remove the build cache's entries another build of the compiler wrote, and
                     the packages, entries and evaluated manifests no build has used in
                     --max-age days (default 30); a build makes them again if it needs them
          clean      remove the whole cache
          warm       build the runtime and the standard library for a target and profile now,
                     rather than in the first build that needs them: the library alone, and
                     with each module (or each --import) a program may import

            --target <triple>   the target (default: this machine)
            --profile <name>    dev or release (default: dev)
            --import <Module>   warm the library as a program importing this module uses it;
                                more than one, together (default: each module, one at a time)
            --into <dir>        write the entries to <dir> rather than $SABLE_HOME/cache: what a
                                release of the compiler carries in a cache/ beside it, read as prebuilt

        The cache is $SABLE_HOME/cache (default ~/.sable/cache). Each build cache entry is keyed
        by the compiler's own build, so a rebuilt compiler uses none of an earlier one's.
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        switch (arguments) {
            case ["list"]:
                return Guard(() => {
                    var environment = Environment;
                    var packages = PackageCache.List(environment);
                    foreach (var package in packages) {
                        Console.WriteLine($"{Path.GetFileName(package.Directory),-40} {PackageCache.Describe(package.Bytes),10}  used {package.LastUsed.ToLocalTime():yyyy-MM-dd}");
                    }

                    var entries = environment.BuildCache.Entries();
                    foreach (var entry in entries) {
                        var what = $"{entry.Package} {entry.Kind} for {entry.Target}{(entry.Profile.Length > 0 ? $" ({entry.Profile})" : "")}";
                        Console.WriteLine($"{Path.GetFileName(entry.Directory)}  {what,-60} {PackageCache.Describe(entry.Bytes),10}  used {entry.LastUsed.ToLocalTime():yyyy-MM-dd}{(entry.Current ? "" : "  (another compiler's)")}");
                    }

                    Console.WriteLine($"{packages.Count} package(s), {PackageCache.Describe(packages.Sum(static p => p.Bytes))}; "
                        + $"{entries.Count} build entr{(entries.Count == 1 ? "y" : "ies")}, {PackageCache.Describe(entries.Sum(static e => e.Bytes))}; in {environment.CacheDirectory}");
                    return 0;
                });
            case ["prune", ..]:
                var days = 30;
                if (arguments is ["prune", "--max-age", var text] && (!int.TryParse(text, out days) || days < 0) || arguments is not (["prune"] or ["prune", "--max-age", _])) {
                    return BadUsage();
                }

                return Guard(() => {
                    var environment = Environment;
                    var (entries, entryBytes) = environment.BuildCache.Prune(TimeSpan.FromDays(days));
                    var (packages, packageBytes) = PackageCache.Collect(environment, TimeSpan.FromDays(days));
                    foreach (var path in entries.Concat(packages)) {
                        environment.Status.WriteLine($"    Removing {path}");
                    }

                    environment.Status.WriteLine($"     Removed {entries.Count + packages.Count} item(s), {PackageCache.Describe(entryBytes + packageBytes)}");
                    return 0;
                });
            case ["clean"]:
                return Guard(() => {
                    var environment = Environment;
                    var bytes = environment.BuildCache.Clean() + PackageCache.Clear(environment);
                    environment.Status.WriteLine($"     Removed the cache, {PackageCache.Describe(bytes)}");
                    return 0;
                });
            case ["warm", ..]:
                return Warm(arguments.Skip(1).ToList());
            default:
                return BadUsage();
        }
    }

    int Warm(List<string> arguments) {
        NativeTarget? target = null;
        var profile = Profile.Dev;
        var imports = new List<string>();
        string? into = null;
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--target" when i + 1 < arguments.Count:
                    if (NativeTarget.Parse(arguments[++i], out var error) is not { } parsed) {
                        Console.Error.WriteLine($"sable: {error}");
                        return 2;
                    }

                    target = parsed;
                    break;
                case "--profile" when i + 1 < arguments.Count:
                    switch (arguments[++i]) {
                        case "dev": profile = Profile.Dev; break;
                        case "release": profile = Profile.Release; break;
                        default: return BadUsage();
                    }

                    break;
                case "--import" when i + 1 < arguments.Count:
                    imports.Add(arguments[++i]);
                    break;
                case "--into" when i + 1 < arguments.Count:
                    into = arguments[++i];
                    break;
                default:
                    return BadUsage();
            }
        }

        return Guard(() => {
            var environment = Environment;
            if (!NativeObjects.CompilerAvailable) {
                environment.Error.WriteLine($"error: cannot run '{NativeBuild.CompilerPath}'; set CC to a clang that can link for this platform");
                return 1;
            }

            var built = target ?? NativeTarget.Host;
            if (!built.HasRuntime) {
                throw new PackageException(NativeBuild.NoRuntime(built));
            }

            var unknown = imports.Where(static m => !Library.ModuleNames.Contains(m) && m != Library.Core).ToList();
            if (unknown.Count > 0) {
                throw new PackageException($"no library module {string.Join(", ", unknown.Select(static m => $"'{m}'"))}; the modules are {string.Join(", ", Library.ModuleNames.Order(StringComparer.Ordinal))}");
            }

            var cache = into is null ? environment.BuildCache : new BuildCache(into);
            var settings = new BuildSettings(profile.Name, profile.Optimise, profile.Debug ? LibraryUnit.DebugDirectory : null, profile.Exclusivity) {
                Native = profile.Native with { Target = target },
                ArcChecks = profile.ArcChecks,
                DebugAssertions = profile.DebugAssertions,
                OverflowChecks = profile.OverflowChecks,
            };
            if (!settings.Separate) {
                throw new PackageException($"the {profile.Name} profile compiles each program whole, with nothing kept to warm");
            }

            // The library alone, then as each module brings in more of it.
            List<IReadOnlyList<string>> sets = imports.Count > 0 ? [imports] : [[], ..Library.ModuleNames.Order(StringComparer.Ordinal).Select(static m => (IReadOnlyList<string>)[m])];
            foreach (var set in sets) {
                environment.Status.WriteLine($"     Warming the standard library{(set.Count > 0 ? $" with {string.Join(", ", set)}" : "")} for {built.Triple} ({profile.Name})");
                var warmed = IncrementalBuild.Warm(set, settings, cache);
                if (!warmed.Succeeded) {
                    foreach (var error in warmed.Errors) {
                        environment.Error.WriteLine(error);
                    }

                    return 1;
                }
            }

            environment.Status.WriteLine($"    Finished warming {cache.Root}");
            return 0;
        });
    }
}
