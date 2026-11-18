using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable search &lt;query&gt;</c>: the registry's packages whose names or descriptions match.</summary>
public sealed class SearchCommand : PackageCommand {
    public override string Name => "search";
    public override string Summary => "search the registry for packages";

    public override string Usage => """
        usage: sable search <query> [--limit <n>] [--registry <url or directory>]

          query       words a package's name or description holds
          --limit n   at most n results (default 20)
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        if (!TakeRegistry(rest, out var location)) {
            return BadUsage();
        }

        var limit = 20;
        var at = rest.IndexOf("--limit");
        if (at >= 0) {
            if (at + 1 >= rest.Count || !int.TryParse(rest[at + 1], out limit) || limit < 1) {
                return BadUsage();
            }

            rest.RemoveRange(at, 2);
        }

        if (rest.Count == 0 || rest.Any(static a => a.StartsWith('-'))) {
            return BadUsage();
        }

        return Guard(() => {
            var registry = RegistryHere(location);
            if (!registry.IsAvailable) {
                throw new PackageException(registry.WhyUnavailable);
            }

            var hits = registry.Search(string.Join(' ', rest), limit);
            foreach (var hit in hits) {
                Console.WriteLine($"{hit.Name} = \"{hit.Version}\"{(hit.Description is { Length: > 0 } d ? $"    # {d}" : "")}");
            }

            if (hits.Count == 0) {
                Console.Error.WriteLine($"no packages match '{string.Join(' ', rest)}'");
            }

            return 0;
        });
    }
}

/// <summary><c>sable login</c>: keeps a registry's token for publishing, yanking and changing owners.</summary>
public sealed class LoginCommand : PackageCommand {
    public override string Name => "login";
    public override string Summary => "keep a network registry's token, for publishing to it";

    public override string Usage => """
        usage: sable login [--registry <url>] [--token <token>]

          The token is read from standard input unless given, so that it stays out of the
          shell's history. It is checked with the registry, then kept in
          $SABLE_HOME/credentials.json, readable only by you.
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        if (!TakeRegistry(rest, out var location)) {
            return BadUsage();
        }

        string? token = null;
        if (rest is ["--token", var given]) {
            token = given;
        } else if (rest.Count > 0) {
            return BadUsage();
        }

        return Guard(() => {
            var url = location ?? (RegistryHere(null) is HttpRegistry here ? here.Location : null)
                ?? throw new PackageException("the registry here is a directory, which takes no login: name a network registry with --registry <url>");
            if (!HttpRegistry.IsUrl(url)) {
                throw new PackageException($"'{url}' is a directory, which takes no login: name a network registry's URL");
            }

            if (token is null) {
                if (!Console.IsInputRedirected) {
                    Console.Error.Write($"token for {url}: ");
                }

                token = Console.ReadLine()?.Trim();
            }

            if (string.IsNullOrEmpty(token)) {
                throw new PackageException("no token was given");
            }

            using var registry = new HttpRegistry(url, token, Environment.Offline);
            var user = registry.WhoAmI();
            Credentials.Save(Environment.Home, registry.Location, token);
            Environment.Status.WriteLine($"   Logged in to {registry.Location} as {user}");
            return 0;
        });
    }
}

/// <summary><c>sable logout</c>: forgets a registry's token.</summary>
public sealed class LogoutCommand : PackageCommand {
    public override string Name => "logout";
    public override string Summary => "forget a network registry's token";
    public override string Usage => "usage: sable logout [--registry <url>]";

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        if (!TakeRegistry(rest, out var location) || rest.Count > 0) {
            return BadUsage();
        }

        return Guard(() => {
            var url = location ?? RegistryHere(null).Location;
            Environment.Status.WriteLine(Credentials.Remove(Environment.Home, url)
                ? $"  Logged out of {url}"
                : $"  There was no token for {url}");
            return 0;
        });
    }
}

/// <summary>
///     <c>sable owner</c>: who may publish a package. Owners are publishing
///     keys — the key signs every version — and an owner adds or removes
///     another by its public key, which <c>sable owner --key</c> prints.
/// </summary>
public sealed class OwnerCommand : PackageCommand {
    public override string Name => "owner";
    public override string Summary => "list, add or remove the owners of a published package";

    public override string Usage => """
        usage: sable owner [<package>] [--list] [--registry <url or directory>]
               sable owner [<package>] --add <public key> | --remove <public key>
               sable owner --key

          <package>   the package (default: the one in this directory)
          --list      its owners' public keys (the default)
          --add       let that key publish it too; you must be an owner
          --remove    stop that key publishing it; what it signed stays valid
          --key       print your public key (from ~/.sable/key, made if there is none),
                      for an owner to add
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        if (!TakeRegistry(rest, out var location)) {
            return BadUsage();
        }

        if (rest is ["--key"]) {
            return Guard(() => {
                using var key = Registry.LoadOrCreateKey(Environment.KeyPath, Environment.Status);
                Console.WriteLine(Registry.PublicKeyOf(key));
                return 0;
            });
        }

        string? package = null;
        if (rest.Count > 0 && !rest[0].StartsWith('-')) {
            package = rest[0];
            rest.RemoveAt(0);
        }

        (string Owner, bool Add)? change = rest switch {
            [] or ["--list"] => null,
            ["--add", var key] => (key, true),
            ["--remove", var key] => (key, false),
            _ => (null!, false),
        };
        if (change is { Owner: null }) {
            return BadUsage();
        }

        return Guard(() => {
            package ??= Member(LoadWorkspace(), null).Name;
            var registry = RegistryHere(location);
            if (change is var (owner, add)) {
                using var key = File.Exists(Environment.KeyPath)
                    ? Registry.LoadOrCreateKey(Environment.KeyPath)
                    : throw new PackageException($"there is no publishing key at {Environment.KeyPath}; only an owner may change the owners");
                registry.ChangeOwner(package, owner, add, key);
                Environment.Status.WriteLine($"{(add ? "      Added" : "    Removed")} an owner of {package}");
            }

            var mine = File.Exists(Environment.KeyPath) ? PublicKey() : null;
            foreach (var key in registry.Owners(package)) {
                Console.WriteLine(key == mine ? $"{key} (you)" : key);
            }

            return 0;
        });
    }

    static string PublicKey() {
        using var key = Registry.LoadOrCreateKey(Environment.KeyPath);
        return Registry.PublicKeyOf(key);
    }
}

/// <summary><c>sable install</c>: a registry package's binaries, built and put in <c>$SABLE_HOME/bin</c>.</summary>
public sealed class InstallCommand : PackageCommand {
    public override string Name => "install";
    public override string Summary => "build a published package's binaries and put them in ~/.sable/bin";

    public override string Usage => """
        usage: sable install <package>[@<version>] [--bin <name>] [--force] [--registry <url or directory>]
               sable install --list

          @<version>   a version requirement (default: the newest release)
          --bin        only this binary
          --force      replace binaries of the same names that another package installed
          --list       what is installed
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        if (!TakeRegistry(rest, out var location)) {
            return BadUsage();
        }

        if (rest is ["--list"]) {
            return Guard(() => {
                foreach (var package in Installer.Load(Environment)) {
                    Console.WriteLine($"{package.Name} v{package.Version}: {string.Join(", ", package.Binaries)}");
                }

                return 0;
            });
        }

        var force = rest.Remove("--force");
        string? bin = null;
        var at = rest.IndexOf("--bin");
        if (at >= 0) {
            if (at + 1 >= rest.Count) {
                return BadUsage();
            }

            bin = rest[at + 1];
            rest.RemoveRange(at, 2);
        }

        if (rest is not [var specification] || specification.StartsWith('-')) {
            return BadUsage();
        }

        return Guard(() => {
            Installer.Install(Environment, RegistryHere(location), specification, bin, force);
            return 0;
        });
    }
}

/// <summary><c>sable uninstall &lt;package&gt;</c>: removes what <c>sable install</c> installed.</summary>
public sealed class UninstallCommand : PackageCommand {
    public override string Name => "uninstall";
    public override string Summary => "remove the binaries 'sable install' installed for a package";
    public override string Usage => "usage: sable uninstall <package>";

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments is not [var name] || name.StartsWith('-')) {
            return BadUsage();
        }

        return Guard(() => {
            Installer.Uninstall(Environment, name);
            return 0;
        });
    }
}

/// <summary><c>sable cache</c>: what the package cache holds, and collecting what is no longer used.</summary>
public sealed class CacheCommand : PackageCommand {
    public override string Name => "cache";
    public override string Summary => "list or collect the package cache";

    public override string Usage => """
        usage: sable cache list
               sable cache gc [--max-age <days>]
               sable cache clean

          list       the fetched packages, with their sizes and when a build last used them
          gc         remove packages no build has used, and evaluated manifests not made, in
                     --max-age days (default 30); a build fetches them again if it needs them
          clean      remove the whole cache
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        switch (arguments) {
            case ["list"]:
                return Guard(() => {
                    var packages = PackageCache.List(Environment);
                    foreach (var package in packages) {
                        Console.WriteLine($"{Path.GetFileName(package.Directory),-40} {PackageCache.Describe(package.Bytes),10}  used {package.LastUsed.ToLocalTime():yyyy-MM-dd}");
                    }

                    Console.WriteLine($"{packages.Count} package(s), {PackageCache.Describe(packages.Sum(static p => p.Bytes))}, in {Environment.CacheDirectory}");
                    return 0;
                });
            case ["gc", ..]:
                var days = 30;
                if (arguments is ["gc", "--max-age", var text] && (!int.TryParse(text, out days) || days < 0) || arguments is not (["gc"] or ["gc", "--max-age", _])) {
                    return BadUsage();
                }

                return Guard(() => {
                    var (removed, bytes) = PackageCache.Collect(Environment, TimeSpan.FromDays(days));
                    foreach (var path in removed) {
                        Environment.Status.WriteLine($"    Removing {path}");
                    }

                    Environment.Status.WriteLine($"     Removed {removed.Count} item(s), {PackageCache.Describe(bytes)}");
                    return 0;
                });
            case ["clean"]:
                return Guard(() => {
                    Environment.Status.WriteLine($"     Removed the cache, {PackageCache.Describe(PackageCache.Clear(Environment))}");
                    return 0;
                });
            default:
                return BadUsage();
        }
    }
}

/// <summary>
///     <c>sable registry</c>: runs a network registry (<see cref="RegistryServer" />)
///     and issues its tokens — for a team's or a company's own registry, and
///     the reference for the protocol.
/// </summary>
public sealed class RegistryCommand : PackageCommand {
    public override string Name => "registry";
    public override string Summary => "serve a network registry, and issue its tokens";

    public override string Usage => """
        usage: sable registry serve --root <dir> [--listen <url>]
               sable registry token --root <dir> --user <name> [--org <org>]... [--reserved]

          serve      serve the registry kept under <dir> (default address http://127.0.0.1:8742/);
                     put it behind a proxy that terminates TLS
          token      issue a token for <name>, who may publish <name>/…, <org>/… for each --org,
                     and with --reserved, unprefixed names (the standard ecosystem's);
                     the token is printed once, and only its hash is kept
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        string? Take(string option) {
            var at = rest.IndexOf(option);
            if (at < 0 || at + 1 >= rest.Count) {
                return null;
            }

            var value = rest[at + 1];
            rest.RemoveRange(at, 2);
            return value;
        }

        if (rest.Count == 0) {
            return BadUsage();
        }

        var action = rest[0];
        rest.RemoveAt(0);
        var root = Take("--root");
        if (root is null) {
            return BadUsage();
        }

        switch (action) {
            case "serve": {
                var listen = Take("--listen") ?? "http://127.0.0.1:8742/";
                if (rest.Count > 0) {
                    return BadUsage();
                }

                return Guard(() => {
                    using var server = new RegistryServer(Path.GetFullPath(root), listen.EndsWith('/') ? listen : listen + "/") { Log = Console.Out };
                    server.Start();
                    Console.Error.WriteLine($"     Serving the registry in {Path.GetFullPath(root)} at {server.Prefix}");
                    server.Wait();
                    return 0;
                });
            }
            case "token": {
                var user = Take("--user");
                var organisations = new List<string>();
                while (Take("--org") is { } organisation) {
                    organisations.Add(organisation);
                }

                var reserved = rest.Remove("--reserved");
                if (user is null || rest.Count > 0) {
                    return BadUsage();
                }

                return Guard(() => {
                    Console.WriteLine(RegistryTokens.Issue(Path.GetFullPath(root), new(user, organisations, reserved)));
                    return 0;
                });
            }
            default:
                return BadUsage();
        }
    }
}
