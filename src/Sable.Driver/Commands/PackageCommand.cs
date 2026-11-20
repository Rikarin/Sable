using Sable.CodeGen;
using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary>
///     A command that works on the package (or workspace) in the current
///     directory or the nearest one above it with a <c>sable.toml</c>.
///     Errors about manifests, resolution and the registry are reported, not
///     thrown.
/// </summary>
public abstract class PackageCommand : Command {
    /// <summary>Runs <paramref name="action" />, turning the package system's errors into messages and exit code 1.</summary>
    protected static int Guard(Func<int> action) {
        try {
            return action();
        } catch (ManifestException e) {
            Console.Error.WriteLine(e.Message);
        } catch (ResolutionException e) {
            Console.Error.Write(e.Message);
        } catch (Exception e) when (e is PackageException or RegistryException or IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"error: {e.Message}");
        }

        return 1;
    }

    protected static PackageEnvironment Environment => PackageEnvironment.FromProcess();

    protected static Workspace LoadWorkspace() => Workspace.Load(Directory.GetCurrentDirectory());

    /// <summary>
    ///     The registry a command talks to: <c>--registry</c>'s, else the
    ///     workspace's here (its <c>[registry] path</c>), else
    ///     <c>$SABLE_REGISTRY</c>'s, else <c>~/.sable/registry</c>.
    /// </summary>
    protected static IRegistry RegistryHere(string? location) =>
        location is not null ? Environment.Open(location)
        : Environment.RegistryFor(Workspace.FindManifestDirectory(Directory.GetCurrentDirectory()) is null ? null : LoadWorkspace());

    /// <summary>Takes <c>--registry &lt;location&gt;</c> out of the arguments; false when it has no value.</summary>
    protected static bool TakeRegistry(List<string> arguments, out string? location) {
        location = null;
        var at = arguments.IndexOf("--registry");
        if (at < 0) {
            return true;
        }

        if (at + 1 >= arguments.Count) {
            return false;
        }

        location = arguments[at + 1];
        arguments.RemoveRange(at, 2);
        return true;
    }

    /// <summary>The member a command acts on: <c>-p</c>'s, the current one, or the only one.</summary>
    protected static LocalPackage Member(Workspace workspace, string? name) =>
        name is not null ? workspace.Member(name)
        : workspace.Current ?? (workspace.Members is [var only] ? only : throw new PackageException("this is a workspace of several packages: say which with -p <package>"));

    /// <summary>
    ///     Reads the package-mode options of <c>build</c>, <c>run</c> and
    ///     <c>check</c>; null on an argument it does not know. What follows
    ///     <c>--</c> is the program's, when <paramref name="programArguments" /> takes it.
    /// </summary>
    internal static BuildRequest? ParseBuildRequest(IReadOnlyList<string> args, List<string>? programArguments = null) {
        var request = new BuildRequest();
        var features = new List<string>();
        var noDefault = false;
        var all = false;
        for (var i = 0; i < args.Count; i++) {
            var argument = args[i];
            string? Value() => i + 1 < args.Count ? args[++i] : null;
            switch (argument) {
                case "--" when programArguments is not null:
                    programArguments.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                case "--release" or "-r":
                    request = request with { Profile = "release" };
                    break;
                case "--profile":
                    if (Value() is not { } profile) {
                        return null;
                    }

                    request = request with { Profile = profile };
                    break;
                case "--locked":
                    request = request with { Locked = true };
                    break;
                case "--timings":
                    request = request with { Timings = true };
                    break;
                case "--no-default-features":
                    noDefault = true;
                    break;
                case "--all-features":
                    all = true;
                    break;
                case "--features" or "-F":
                    if (Value() is not { } list) {
                        return null;
                    }

                    features.AddRange(list.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "-p" or "--package":
                    if (Value() is not { } package) {
                        return null;
                    }

                    request = request with { Package = package };
                    break;
                case "--bin":
                    if (Value() is not { } bin) {
                        return null;
                    }

                    request = request with { Bin = bin };
                    break;
                case "--target":
                    if (Value() is not { } triple) {
                        return null;
                    }

                    if (NativeTarget.Parse(triple, out var error) is not { } target) {
                        Console.Error.WriteLine($"sable: {error}");
                        return null;
                    }

                    request = request with { Target = target };
                    break;
                case "--sysroot":
                    if (Value() is not { } sysroot) {
                        return null;
                    }

                    request = request with { Sysroot = sysroot };
                    break;
                case "--deny-reflection":
                    request = request with { DenyReflection = true };
                    break;
                case "--deny-unsafe":
                    request = request with { DenyUnsafe = true };
                    break;
                default:
                    if (argument.StartsWith("--features=", StringComparison.Ordinal)) {
                        features.AddRange(argument["--features=".Length..].Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
                        break;
                    }

                    return null;
            }
        }

        return request with { Features = new(features, noDefault, all) };
    }

    /// <summary>
    ///     Whether <c>build</c> or <c>run</c> should work on a package: no
    ///     <c>.sb</c> file is named (before any <c>--</c>).
    /// </summary>
    internal static bool IsPackageMode(IReadOnlyList<string> args) =>
        !args.TakeWhile(static a => a != "--").Any(static a => a.EndsWith(".sb", StringComparison.Ordinal));

    /// <summary><c>sable build</c> on a package.</summary>
    internal static int BuildPackage(IReadOnlyList<string> args, string usage) {
        if (ParseBuildRequest(args) is not { } request) {
            Console.Error.WriteLine(usage);
            return 2;
        }

        return Guard(() => PackageBuilder.Build(LoadWorkspace(), Environment, request) is null ? 1 : 0);
    }

    /// <summary><c>sable run</c> on a package.</summary>
    internal static int RunPackage(IReadOnlyList<string> args, string usage) {
        var programArguments = new List<string>();
        if (ParseBuildRequest(args, programArguments) is not { } request) {
            Console.Error.WriteLine(usage);
            return 2;
        }

        return Guard(() => PackageBuilder.Run(LoadWorkspace(), Environment, request, programArguments));
    }

    internal const string BuildOptions = """
          --release, -r            the release profile (default: dev); output in target/<profile>/
          --profile <name>         the manifest's profile of that name: dev, release, or one of its own
          --features <a,b>         turn on these features of the package
          --no-default-features    leave its default features off
          --all-features           turn on every feature
          -p, --package <name>     the workspace member to build (default: the one you are in, else all)
          --bin <name>             only this binary
          --locked                 fail rather than change sable.lock
          --timings                print the time each phase of the build took
          --target <triple>        build for another machine (aarch64-unknown-linux-gnu, x86_64-apple-darwin,
                                   ...); output in target/<triple>/<profile>/
          --sysroot <dir>          where the target's C library is (default: $SABLE_HOME/sysroots/<triple>,
                                   or for WASI, wasi-sdk's or wasi-libc's where they are installed)
          --deny-reflection        carry no reflection (also [targets.<triple>] reflection = false)
          --deny-unsafe            fail on any 'unsafe' block or 'unsafe fn' in the workspace's own
                                   packages (S3472); dependencies may use 'unsafe'
        """;
}
