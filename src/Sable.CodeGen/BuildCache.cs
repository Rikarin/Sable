using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Sable.CodeGen;

/// <summary>
///     Which compiler this is: a hash of its own build — every assembly of
///     it, the resources they carry (the standard library's source, the
///     runtime's) and its version — so that a compiler rebuilt with any
///     change, even under the same version number, is another compiler, and
///     uses nothing an earlier build of it put in the <see cref="BuildCache" />.
/// </summary>
public static class CompilerIdentity {
    /// <summary>The assemblies the compiler is: the driver (<c>sable</c>) and the layers under it.</summary>
    public static readonly IReadOnlyList<string> AssemblyNames = ["sable", "Sable.CodeGen", "Sable.SIL", "Sable.Symbols", "Sable.Syntax"];

    /// <summary>This compiler's identity, worked out once per process.</summary>
    public static string Current => Computed.Value;

    static readonly Lazy<string> Computed = new(static () => Of(Parts()));

    /// <summary>The compiler's version: the driver's, without its build metadata; the code generator's when there is no driver.</summary>
    public static string Version {
        get {
            var assembly = Load("sable") ?? typeof(CompilerIdentity).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }

    /// <summary>The identity of a compiler made of <paramref name="parts" />, each a name and its content, in order.</summary>
    public static string Of(IEnumerable<(string Name, byte[] Content)> parts) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("sable-compiler-identity-1\n"u8);
        foreach (var (name, content) in parts) {
            hash.AppendData(Encoding.UTF8.GetBytes($"{name}\n{content.Length}\n"));
            hash.AppendData(content);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    ///     What the identity is a hash of: the version, then for each of the
    ///     compiler's assemblies its file — or, where it has none on disk (a
    ///     single-file build), its module's id, which a deterministic build
    ///     derives from all that went into it — and every resource it embeds,
    ///     by name: <c>lib/</c>'s source and the runtime's among them.
    /// </summary>
    static IEnumerable<(string, byte[])> Parts() {
        yield return ("version", Encoding.UTF8.GetBytes(Version));
        foreach (var name in AssemblyNames) {
            if (Load(name) is not { } assembly) {
                yield return (name, []);
                continue;
            }

            yield return (name, assembly.Location is { Length: > 0 } location && File.Exists(location)
                ? File.ReadAllBytes(location)
                : assembly.ManifestModule.ModuleVersionId.ToByteArray());
            foreach (var resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal)) {
                using var stream = assembly.GetManifestResourceStream(resource);
                using var content = new MemoryStream();
                stream?.CopyTo(content);
                yield return ($"{name}:{resource}", content.ToArray());
            }
        }
    }

    static Assembly? Load(string name) {
        if (AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name) is { } loaded) {
            return loaded;
        }

        try {
            return Assembly.Load(new AssemblyName(name));
        } catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) {
            return null;
        }
    }
}

/// <summary>
///     What a <see cref="BuildCache" /> entry is keyed by (#182): everything
///     that decides what is built. The entry's directory is named by a hash
///     of all of it, so a cached entry is only ever the one this build would make.
/// </summary>
/// <param name="Kind"><c>library</c> for a library's unit, <c>runtime</c> for the C runtime.</param>
/// <param name="Package">The package — <c>sable-std</c> for the standard library, <c>sable-runtime</c> for the runtime.</param>
/// <param name="SourceChecksum">A hash of the source compiled: every file's name and text.</param>
/// <param name="CompilerIdentity">The compiler's <see cref="CodeGen.CompilerIdentity" />.</param>
/// <param name="Target">The target triple.</param>
/// <param name="Options">
///     The build profile, as the settings of it that change code — the
///     opt-level, debug information, link-time optimisation, the checks
///     kept, <c>DEBUG</c> or <c>RELEASE</c> — and the C compiler's identity,
///     each <c>name=value</c>. Not the profile's name: two profiles that
///     compile code the same way share their entries.
/// </param>
/// <param name="Features">The package's enabled features.</param>
/// <param name="Dependencies">The keys (<see cref="Hash" />) of the entries it was compiled against.</param>
public sealed record CacheKey(
    string Kind,
    string Package,
    string Version,
    string SourceChecksum,
    string CompilerIdentity,
    string Target,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Dependencies
) {
    /// <summary>The format of the key and of an entry: bump when either changes meaning.</summary>
    public const string Format = "sable-cache-1";

    /// <summary>The key as text, one field a line: what is hashed, and what the entry records.</summary>
    public string Text => string.Join("\n", [
        Format,
        $"kind={Kind}",
        $"package={Package}",
        $"version={Version}",
        $"source={SourceChecksum}",
        $"compiler={CompilerIdentity}",
        $"target={Target}",
        ..Options.Select(static o => "option " + o),
        $"features={string.Join(",", Features.Order(StringComparer.Ordinal))}",
        ..Dependencies.Select(static d => "dependency " + d),
    ]);

    /// <summary>The entry's directory name: 32 hex digits of the SHA-256 of <see cref="Text" />.</summary>
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)))[..32];

    /// <summary>A hash of text, for a key's <see cref="SourceChecksum" /> and options.</summary>
    public static string Checksum(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>An entry of the <see cref="BuildCache" />, as <c>sable cache list</c> shows it.</summary>
/// <param name="Current">Whether this compiler wrote it: an entry of another is never used, and <c>prune</c> removes it.</param>
public sealed record CacheEntry(string Directory, string Kind, string Package, string Target, string Profile, bool Current, DateTime LastUsed, long Bytes);

/// <summary>
///     The build cache (#182): what a build makes that does not depend on the
///     program — the C runtime's archive, the standard library's archive and
///     its records — kept under <c>$SABLE_HOME/cache/&lt;key&gt;/</c> for every
///     build of every project to share, each entry built lazily the first time
///     a target and profile needs it.
/// </summary>
/// <remarks>
///     <para>
///         An entry is a directory named by its <see cref="CacheKey" />'s hash,
///         with an <c>entry.txt</c> recording the key and what built it. It is
///         made under a temporary name and renamed into place
///         (<see cref="AtomicDirectory" />), so builds running at once and the
///         language server see it whole or not at all; two that build the same
///         entry at once each make it, and the first to rename keeps it.
///     </para>
///     <para>
///         The compiler's own identity is part of every key, so a rebuilt
///         compiler finds none of the entries an earlier build of it wrote, and
///         <see cref="Prune" /> removes them. A release of the compiler can
///         carry entries prebuilt for its host (<c>sable cache warm --into</c>)
///         in a <c>cache/</c> directory beside it, which are read but never written.
///     </para>
///     <para>
///         The same directory holds the fetched packages (<c>PackageCache</c>),
///         each named for its package and with a checksum file in it; an
///         entry is told apart by its <c>entry.txt</c>.
///     </para>
/// </remarks>
public sealed class BuildCache {
    /// <summary>The file in every entry that records its key and makes it an entry.</summary>
    public const string EntryFile = "entry.txt";

    /// <summary>Touched, at most once a day, whenever a build uses an entry: what <see cref="Prune" /> goes by.</summary>
    const string UsedFile = ".used";

    /// <param name="root">Where entries are written: by default <c>$SABLE_HOME/cache</c>.</param>
    /// <param name="identity">The compiler's identity; by default this one's.</param>
    /// <param name="prebuilt">Further roots, read but never written: a release's prebuilt entries.</param>
    public BuildCache(string root, string? identity = null, IReadOnlyList<string>? prebuilt = null) {
        Root = Path.GetFullPath(root);
        Identity = identity ?? CompilerIdentity.Current;
        Prebuilt = prebuilt ?? [];
    }

    /// <summary>The user's cache, <c>$SABLE_HOME/cache</c>, with what the release carries beside the compiler.</summary>
    public static BuildCache Default => new(Path.Combine(NativeBuild.SableHome, "cache"), prebuilt: [Path.Combine(AppContext.BaseDirectory, "cache")]);

    public string Root { get; }

    public string Identity { get; }

    public IReadOnlyList<string> Prebuilt { get; }

    /// <summary>A key whose compiler is this cache's.</summary>
    public CacheKey Key(string kind, string package, string version, string sourceChecksum, string target, IReadOnlyList<string> options, IReadOnlyList<string>? features = null, IReadOnlyList<string>? dependencies = null) =>
        new(kind, package, version, sourceChecksum, Identity, target, options, features ?? [], dependencies ?? []);

    /// <summary>Where the entry for <paramref name="key" /> is: a prebuilt one when there is one, else where this cache writes it.</summary>
    public string DirectoryFor(CacheKey key) {
        var name = key.Hash;
        foreach (var prebuilt in Prebuilt) {
            var candidate = Path.Combine(prebuilt, name);
            if (IsEntry(candidate, key)) {
                return candidate;
            }
        }

        return Path.Combine(Root, name);
    }

    /// <summary>
    ///     The entry for <paramref name="key" />, made by <paramref name="make" />
    ///     in an empty directory when it is not there, and published whole; its
    ///     directory, and how making it went. A build that uses it marks it used.
    /// </summary>
    /// <param name="profile">The name of the profile the build that makes it was for: recorded, not keyed.</param>
    public (string Directory, NativeBuildResult Result) GetOrCreate(CacheKey key, string profile, Func<string, NativeBuildResult> make) {
        if (key.CompilerIdentity != Identity) {
            throw new ArgumentException("the key is for another compiler", nameof(key));
        }

        var directory = DirectoryFor(key);
        var result = AtomicDirectory.GetOrCreate(directory, temporary => {
            var made = make(temporary);
            if (made.Succeeded) {
                File.WriteAllText(Path.Combine(temporary, EntryFile), $"{key.Text}\nprofile={profile}\ncreated={DateTime.UtcNow:O}\n");
            }

            return made;
        });
        if (result.Succeeded) {
            MarkUsed(directory);
        }

        return (directory, result);
    }

    /// <summary>Whether <paramref name="directory" /> holds the entry <paramref name="key" /> names.</summary>
    static bool IsEntry(string directory, CacheKey key) {
        var file = Path.Combine(directory, EntryFile);
        return File.Exists(file) && File.ReadAllText(file).StartsWith(key.Text + "\n", StringComparison.Ordinal);
    }

    /// <summary>Notes that a build used the entry in <paramref name="directory" />: touched at most once a day.</summary>
    public static void MarkUsed(string directory) {
        var stamp = Path.Combine(directory, UsedFile);
        try {
            if (!File.Exists(stamp)) {
                File.WriteAllText(stamp, "");
            } else if (File.GetLastWriteTimeUtc(stamp) < DateTime.UtcNow.AddDays(-1)) {
                File.SetLastWriteTimeUtc(stamp, DateTime.UtcNow);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // A prebuilt entry, or a cache that cannot be marked: it is
            // pruned sooner, and built again when it is next needed.
        }
    }

    /// <summary>Every entry in the cache (not the prebuilt ones), least recently used first.</summary>
    public IReadOnlyList<CacheEntry> Entries() {
        if (!Directory.Exists(Root)) {
            return [];
        }

        var entries = new List<CacheEntry>();
        foreach (var directory in Directory.GetDirectories(Root)) {
            var file = Path.Combine(directory, EntryFile);
            if (!File.Exists(file)) {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(file)) {
                if (line.IndexOf('=') is var equals and > 0 && !line.StartsWith("option ", StringComparison.Ordinal) && !line.StartsWith("dependency ", StringComparison.Ordinal)) {
                    fields.TryAdd(line[..equals], line[(equals + 1)..]);
                }
            }

            var stamp = Path.Combine(directory, UsedFile);
            entries.Add(new(
                directory,
                fields.GetValueOrDefault("kind", "?"),
                fields.GetValueOrDefault("package", "?"),
                fields.GetValueOrDefault("target", "?"),
                fields.GetValueOrDefault("profile", ""),
                fields.GetValueOrDefault("compiler") == Identity,
                File.GetLastWriteTimeUtc(File.Exists(stamp) ? stamp : file),
                Size(directory)
            ));
        }

        return [..entries.OrderBy(static e => e.LastUsed)];
    }

    /// <summary>
    ///     Removes the entries another compiler wrote, those no build has used
    ///     in <paramref name="maximumAge" />, and what a build that was stopped
    ///     left half-made more than an hour ago. Returns what was removed and
    ///     how many bytes. Nothing removed is lost: a build makes it again.
    /// </summary>
    public (IReadOnlyList<string> Removed, long Bytes) Prune(TimeSpan maximumAge) {
        var cutoff = DateTime.UtcNow - maximumAge;
        var removed = new List<string>();
        long bytes = 0;
        foreach (var entry in Entries().Where(e => !e.Current || e.LastUsed < cutoff)) {
            bytes += entry.Bytes;
            Remove(entry.Directory);
            removed.Add(entry.Directory);
        }

        foreach (var directory in Interrupted(DateTime.UtcNow.AddHours(-1))) {
            bytes += Size(directory);
            Remove(directory);
            removed.Add(directory);
        }

        return (removed, bytes);
    }

    /// <summary>Removes every entry, and anything half-made. Returns the bytes freed.</summary>
    public long Clean() {
        long bytes = 0;
        foreach (var directory in Entries().Select(static e => e.Directory).Concat(Interrupted(DateTime.MaxValue))) {
            bytes += Size(directory);
            Remove(directory);
        }

        return bytes;
    }

    /// <summary>The entries a build was making when it stopped, last written before <paramref name="before" />.</summary>
    IEnumerable<string> Interrupted(DateTime before) =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root)
                .Where(static d => Path.GetFileName(d) is var name && name.Length > 32 && name[32..].StartsWith(AtomicDirectory.TemporaryMarker, StringComparison.Ordinal))
                .Where(d => Directory.GetLastWriteTimeUtc(d) < before)
                .ToList()
            : [];

    /// <summary>
    ///     Removes an entry: renamed out of the way first, so that a build
    ///     never finds it half-removed, then deleted.
    /// </summary>
    static void Remove(string directory) {
        var doomed = AtomicDirectory.TemporaryFor(directory);
        try {
            Directory.Move(directory, doomed);
        } catch (IOException) {
            return;
        }

        Directory.Delete(doomed, true);
    }

    static long Size(string directory) =>
        Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(static f => f.Length) : 0;
}
