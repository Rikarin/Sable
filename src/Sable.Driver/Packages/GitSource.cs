using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sable.Driver.Packages;

/// <summary>
///     A git dependency at the commit chosen for it, and the directory its
///     package is in: the checkout, or a directory inside it.
/// </summary>
public sealed record GitCheckout(string Url, GitReference? Reference, string Commit, string Directory) {
    /// <summary>How the lock file names it: <c>git+&lt;url&gt;[?tag=v1]#&lt;commit&gt;</c>, as Cargo does.</summary>
    public string Source => $"git+{Url}{(Reference is null ? "" : $"?{Reference}")}#{Commit}";

    /// <summary>The commit as people read one.</summary>
    public string ShortCommit => Commit[..Math.Min(8, Commit.Length)];

    /// <summary>Reads a lock file's <see cref="Source" />; false when it is not a git source.</summary>
    public static bool TryParse(string source, out string url, out GitReference? reference, out string commit) {
        (url, reference, commit) = ("", null, "");
        if (!source.StartsWith("git+", StringComparison.Ordinal)) {
            return false;
        }

        var hash = source.LastIndexOf('#');
        if (hash < 0) {
            return false;
        }

        commit = source[(hash + 1)..];
        url = source[4..hash];
        var query = url.LastIndexOf('?');
        if (query >= 0 && url[(query + 1)..].Split('=', 2) is [var kind and ("tag" or "branch" or "rev"), var value]) {
            reference = new(kind, value);
            url = url[..query];
        }

        return commit.Length > 0 && url.Length > 0;
    }
}

/// <summary>
///     Fetches git dependencies (docs/05 §5.2.4) into the cache and chooses
///     their commits. Each repository is fetched once into a bare clone,
///     <c>cache/git/db/&lt;name&gt;-&lt;hash&gt;</c>, and each commit used is
///     checked out once, <c>cache/git/checkouts/&lt;name&gt;-&lt;hash&gt;/&lt;commit&gt;</c>.
///     A dependency the lock file already pins stays at its commit — no
///     network, if the checkout is on disk — unless it is being updated.
///     Uses the <c>git</c> on PATH, so credentials and proxies are git's own.
/// </summary>
public sealed partial class GitSource(string cacheDirectory, TextWriter output, LockFile? previous = null, IReadOnlySet<string>? update = null) {
    readonly HashSet<string> _fetched = new(StringComparer.Ordinal);
    readonly Dictionary<string, GitCheckout> _checkouts = new(StringComparer.Ordinal);

    /// <summary>The packages that came from git, by name, each with the commit chosen and its directory.</summary>
    public IReadOnlyDictionary<string, GitCheckout> Checkouts => _checkouts;

    /// <summary>Notes that a package came from <paramref name="checkout" />.</summary>
    internal void Record(string name, GitCheckout checkout) => _checkouts[name] = checkout;

    /// <summary>Whether a manifest's <c>git</c> names a repository: a URL git fetches from, or scp-like <c>user@host:path</c>.</summary>
    public static bool IsUrl(string text) => UrlPattern().IsMatch(text);

    [GeneratedRegex(@"^((https?|ssh|git|file)://\S+|[A-Za-z0-9._-]+@[A-Za-z0-9._-]+:\S+)$")]
    private static partial Regex UrlPattern();

    /// <summary>
    ///     The repository at the commit to use for dependency <paramref name="name" />:
    ///     the lock file's, while its URL and reference are the manifest's and
    ///     it is not being updated; else what the reference names now.
    /// </summary>
    /// <exception cref="PackageException">git is missing, cannot fetch, or the reference names nothing.</exception>
    public GitCheckout Checkout(string name, string url, GitReference? reference) {
        var commit = Pinned(name, url, reference) ?? Resolve(name, url, reference);
        var directory = Path.Combine(cacheDirectory, "git", "checkouts", RepositoryName(url), commit);
        if (!Directory.Exists(directory)) {
            Materialise(name, url, commit, directory);
        }

        return new(url, reference, commit, directory);
    }

    string? Pinned(string name, string url, GitReference? reference) {
        if (update is not null && (update.Count == 0 || update.Contains(name))) {
            return null;
        }

        return previous?.Find(name) is { } locked
               && GitCheckout.TryParse(locked.Source, out var lockedUrl, out var lockedReference, out var commit)
               && lockedUrl == url && lockedReference == reference
            ? commit
            : null;
    }

    /// <summary>A directory name for a repository: its last path segment, made safe, and a hash of the whole URL.</summary>
    static string RepositoryName(string url) {
        var last = url.TrimEnd('/').Split('/', ':').Last();
        if (last.EndsWith(".git", StringComparison.Ordinal)) {
            last = last[..^4];
        }

        var safe = new string([..last.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        return safe.Length == 0 ? hash : $"{safe}-{hash}";
    }

    /// <summary>The bare clone of a repository, fetched once per resolution.</summary>
    string Database(string url) {
        var database = Path.Combine(cacheDirectory, "git", "db", RepositoryName(url));
        if (!Directory.Exists(database)) {
            var temporary = database + $".tmp-{Guid.NewGuid():N}";
            Directory.CreateDirectory(temporary);
            Git(null, "init", "--bare", "--quiet", temporary);
            if (!Directory.Exists(database)) {
                Directory.Move(temporary, database);
            } else {
                Directory.Delete(temporary, true);
            }
        }

        if (_fetched.Add(database)) {
            output.WriteLine($"    Updating git repository `{url}`");
            Git(database, "fetch", "--quiet", "--force", "--prune", "--tags", "--", url,
                "+refs/heads/*:refs/heads/*", "+HEAD:refs/sable/HEAD");
        }

        return database;
    }

    /// <summary>The commit a reference names in the repository now.</summary>
    string Resolve(string name, string url, GitReference? reference) {
        var database = Database(url);
        var spec = reference switch {
            null => "refs/sable/HEAD",
            { Kind: "tag" } => $"refs/tags/{reference.Value}",
            { Kind: "branch" } => $"refs/heads/{reference.Value}",
            _ => reference.Value,
        };

        if (TryGit(database, "rev-parse", "--verify", "--quiet", $"{spec}^{{commit}}") is { } commit) {
            return commit;
        }

        // A commit no branch or tag reaches: ask the server for it by name.
        if (reference is { Kind: "rev" } && IsCommitId(reference.Value) && FetchCommit(database, url, reference.Value) is { } fetched) {
            return fetched;
        }

        var what = reference is null ? "a default branch" : $"{reference.Kind} '{reference.Value}'";
        throw new PackageException($"dependency '{name}': the git repository {url} has no {what}");
    }

    /// <summary>Checks a commit out into <paramref name="directory" />, whole or not at all.</summary>
    void Materialise(string name, string url, string commit, string directory) {
        var database = Path.Combine(cacheDirectory, "git", "db", RepositoryName(url));
        if (!Directory.Exists(database) || TryGit(database, "cat-file", "-e", $"{commit}^{{commit}}") is null) {
            database = Database(url);
            if (TryGit(database, "cat-file", "-e", $"{commit}^{{commit}}") is null && FetchCommit(database, url, commit) is null) {
                throw new PackageException($"dependency '{name}': {Workspace.LockName} pins commit {commit}, which the git repository {url} does not have; run 'sable update {name}'");
            }
        }

        output.WriteLine($"    Checking out {name} at {commit[..Math.Min(8, commit.Length)]} from `{url}`");
        var temporary = directory + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        try {
            Git(null, "clone", "--quiet", "--no-checkout", "--", database, temporary);
            Git(temporary, "checkout", "--quiet", "--detach", commit);
            if (File.Exists(Path.Combine(temporary, ".gitmodules"))) {
                Git(temporary, "submodule", "update", "--init", "--recursive", "--quiet");
            }

            if (!Directory.Exists(directory)) {
                Directory.Move(temporary, directory);
            }
        } finally {
            if (Directory.Exists(temporary)) {
                DeleteTree(temporary);
            }
        }
    }

    string? FetchCommit(string database, string url, string commit) =>
        TryGit(database, "fetch", "--quiet", "--", url, commit) is not null
            ? TryGit(database, "rev-parse", "--verify", "--quiet", $"{commit}^{{commit}}")
            : null;

    static bool IsCommitId(string text) => text.Length is 40 or 64 && text.All(char.IsAsciiHexDigit);

    /// <summary>Deletes a directory git wrote, whose object files git makes read-only.</summary>
    static void DeleteTree(string directory) {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, true);
    }

    static string Git(string? directory, params string[] arguments) {
        var (code, output, error) = Run(directory, arguments);
        return code == 0
            ? output
            : throw new PackageException($"git {string.Join(' ', arguments.Where(static a => a != "--quiet"))} failed:\n{error.TrimEnd()}");
    }

    static string? TryGit(string? directory, params string[] arguments) {
        var (code, output, _) = Run(directory, arguments);
        return code == 0 ? output : null;
    }

    static (int Code, string Output, string Error) Run(string? directory, string[] arguments) {
        var start = new ProcessStartInfo("git") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        if (directory is not null) {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);
        }

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        // Never wait on a prompt for credentials nobody will type.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        try {
            using var process = Process.Start(start) ?? throw new PackageException("could not start git");
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output.Trim(), error.Result);
        } catch (Win32Exception) {
            throw new PackageException("git dependencies need git, and there is no 'git' on PATH");
        }
    }
}
