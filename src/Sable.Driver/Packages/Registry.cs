using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.Driver.Packages;

/// <summary>A dependency as the registry's index records it: what a resolver needs without the archive.</summary>
public sealed record IndexDependency(string Name, string Requirement, string Kind, bool Optional, bool DefaultFeatures, IReadOnlyList<string> Features);

/// <summary>One published version of a package.</summary>
public sealed record IndexEntry(
    string Name,
    SemVersion Version,
    string Checksum,
    IReadOnlyList<IndexDependency> Dependencies,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Features,
    bool Yanked,
    string PublicKey,
    string Signature) {
    /// <summary>
    ///     The text a signature covers: everything about the version but
    ///     whether it is yanked. Canonical — sorted, one fact per line — so it
    ///     is the same text wherever it is computed.
    /// </summary>
    public string SignedText() {
        var builder = new StringBuilder("sable-package-v1\n");
        builder.Append("name=").Append(Name).Append('\n');
        builder.Append("version=").Append(Version).Append('\n');
        builder.Append("checksum=").Append(Checksum).Append('\n');
        foreach (var d in Dependencies.OrderBy(static d => d.Kind, StringComparer.Ordinal).ThenBy(static d => d.Name, StringComparer.Ordinal)) {
            builder.Append($"dependency={d.Kind} {d.Name} {d.Requirement} optional={d.Optional} default-features={d.DefaultFeatures} features={string.Join(',', d.Features.Order(StringComparer.Ordinal))}\n");
        }

        foreach (var (feature, values) in Features.OrderBy(static f => f.Key, StringComparer.Ordinal)) {
            builder.Append($"feature={feature} {string.Join(',', values)}\n");
        }

        return builder.ToString();
    }

    /// <summary>The dependency specs this entry stands for, as a manifest would declare them.</summary>
    public IEnumerable<DependencySpec> Specs(string kind) =>
        Dependencies.Where(d => d.Kind == kind).Select(static d => new DependencySpec(d.Name, VersionReq.Parse(d.Requirement), null, null, d.Optional, d.DefaultFeatures, d.Features, 0));
}

/// <summary>Something is wrong with a registry, a package in it, or the cache: a checksum, a signature, a version that exists.</summary>
public sealed class RegistryException(string message) : Exception(message);

/// <summary>
///     The registry (docs/05 §5.5): a directory, content-addressed and
///     append-only. <c>index/&lt;name&gt;.json</c> lists a package's versions,
///     each with its archive's sha256, its dependencies, and an ECDSA P-256
///     signature by its owner's key; <c>blobs/&lt;sha256&gt;.tar</c> holds the
///     archives. A version, once published, is never changed: yanking only
///     marks it unresolvable for new lock files.
/// </summary>
public sealed class Registry(string directory) {
    public string Directory { get; } = directory;

    string IndexPath(string name) => Path.Combine(Directory, "index", Path.Combine(name.Split('/')) + ".json");

    string BlobPath(string checksum) => Path.Combine(Directory, "blobs", checksum.Replace("sha256:", "", StringComparison.Ordinal) + ".tar");

    /// <summary>Whether the registry is there to read: offline, it may not be.</summary>
    public bool IsAvailable => System.IO.Directory.Exists(Path.Combine(Directory, "index"));

    /// <summary>Every version of <paramref name="name" /> published, yanked or not, oldest first.</summary>
    public IReadOnlyList<IndexEntry> Entries(string name) {
        var path = IndexPath(name);
        if (!File.Exists(path)) {
            return [];
        }

        try {
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            return [..root["versions"]!.AsArray().Select(v => ReadEntry(name, v!.AsObject()))];
        } catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException) {
            throw new RegistryException($"the registry's index for '{name}' is damaged: {e.Message}");
        }
    }

    /// <summary>The key that owns <paramref name="name" />: the first publisher's.</summary>
    public string? Owner(string name) {
        var path = IndexPath(name);
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["owner"]?.GetValue<string>() : null;
    }

    public IndexEntry? Entry(string name, SemVersion version) => Entries(name).FirstOrDefault(e => e.Version == version);

    static IndexEntry ReadEntry(string name, JsonObject v) => new(
        name,
        SemVersion.Parse(v["version"]!.GetValue<string>()),
        v["checksum"]!.GetValue<string>(),
        [..v["dependencies"]!.AsArray().Select(static d => new IndexDependency(
            d!["name"]!.GetValue<string>(),
            d["req"]!.GetValue<string>(),
            d["kind"]!.GetValue<string>(),
            d["optional"]!.GetValue<bool>(),
            d["default-features"]!.GetValue<bool>(),
            [..d["features"]!.AsArray().Select(static f => f!.GetValue<string>())]))],
        v["features"]!.AsObject().ToDictionary(static f => f.Key, static f => (IReadOnlyList<string>)[..f.Value!.AsArray().Select(static x => x!.GetValue<string>())], StringComparer.Ordinal),
        v["yanked"]!.GetValue<bool>(),
        v["key"]!.GetValue<string>(),
        v["signature"]!.GetValue<string>());

    static JsonObject WriteEntry(IndexEntry e) => new() {
        ["version"] = e.Version.ToString(),
        ["checksum"] = e.Checksum,
        ["dependencies"] = new JsonArray([..e.Dependencies.Select(static d => (JsonNode)new JsonObject {
            ["name"] = d.Name,
            ["req"] = d.Requirement,
            ["kind"] = d.Kind,
            ["optional"] = d.Optional,
            ["default-features"] = d.DefaultFeatures,
            ["features"] = new JsonArray([..d.Features.Select(static f => (JsonNode)JsonValue.Create(f)!)]),
        })]),
        ["features"] = new JsonObject(e.Features.OrderBy(static f => f.Key, StringComparer.Ordinal).Select(static f =>
            KeyValuePair.Create(f.Key, (JsonNode?)new JsonArray([..f.Value.Select(static x => (JsonNode)JsonValue.Create(x)!)])))),
        ["yanked"] = e.Yanked,
        ["key"] = e.PublicKey,
        ["signature"] = e.Signature,
    };

    void WriteIndex(string name, string owner, IReadOnlyList<IndexEntry> entries) {
        var path = IndexPath(name);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = new JsonObject {
            ["name"] = name,
            ["owner"] = owner,
            ["versions"] = new JsonArray([..entries.Select(static e => (JsonNode)WriteEntry(e))]),
        };

        // Written whole to a temporary file and moved, so a reader never sees half of it.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temporary, path, true);
    }

    /// <summary>Adds a version: its archive and its signed index entry.</summary>
    /// <exception cref="RegistryException">The version exists already, or the key is not the package's owner's.</exception>
    public IndexEntry Publish(IndexEntry unsigned, byte[] archive, ECDsa key) {
        var entries = Entries(unsigned.Name);
        if (entries.Any(e => e.Version == unsigned.Version)) {
            throw new RegistryException($"{unsigned.Name} {unsigned.Version} is already published, and a published version never changes: publish a new version");
        }

        var publicKey = PublicKeyOf(key);
        var owner = Owner(unsigned.Name) ?? publicKey;
        if (owner != publicKey) {
            throw new RegistryException($"{unsigned.Name} is owned by another key; only its owner may publish it");
        }

        var checksum = Checksum(archive);
        if (checksum != unsigned.Checksum) {
            throw new RegistryException("internal error: the archive is not the one the entry describes");
        }

        var entry = unsigned with { PublicKey = publicKey, Signature = "" };
        entry = entry with { Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(entry.SignedText()), HashAlgorithmName.SHA256)) };

        var blob = BlobPath(checksum);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        if (!File.Exists(blob)) {
            File.WriteAllBytes(blob, archive);
        }

        WriteIndex(unsigned.Name, owner, [..entries, entry]);
        return entry;
    }

    /// <summary>Marks a version yanked (or not): no new resolution chooses it; lock files that hold it keep it.</summary>
    public void Yank(string name, SemVersion version, ECDsa key, bool yanked = true) {
        var entries = Entries(name).ToList();
        var index = entries.FindIndex(e => e.Version == version);
        if (index < 0) {
            throw new RegistryException($"{name} {version} is not published");
        }

        var owner = Owner(name)!;
        if (owner != PublicKeyOf(key)) {
            throw new RegistryException($"{name} is owned by another key; only its owner may yank it");
        }

        entries[index] = entries[index] with { Yanked = yanked };
        WriteIndex(name, owner, entries);
    }

    /// <summary>
    ///     The archive of a version, checked: its signature is the owner's and
    ///     covers this entry, and its bytes hash to the checksum signed.
    /// </summary>
    public byte[] Download(IndexEntry entry) {
        Verify(entry);
        var blob = BlobPath(entry.Checksum);
        if (!File.Exists(blob)) {
            throw new RegistryException($"the registry has no archive for {entry.Name} {entry.Version}");
        }

        var bytes = File.ReadAllBytes(blob);
        if (Checksum(bytes) != entry.Checksum) {
            throw new RegistryException($"the archive of {entry.Name} {entry.Version} does not match its checksum: the registry has been tampered with");
        }

        return bytes;
    }

    /// <summary>Checks an entry's signature, and that the key that made it owns the package.</summary>
    public void Verify(IndexEntry entry) {
        if (Owner(entry.Name) is not { } owner || owner != entry.PublicKey) {
            throw new RegistryException($"{entry.Name} {entry.Version} is not signed by the package's owner");
        }

        try {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(entry.PublicKey), out _);
            if (key.VerifyData(Encoding.UTF8.GetBytes(entry.SignedText()), Convert.FromBase64String(entry.Signature), HashAlgorithmName.SHA256)) {
                return;
            }
        } catch (Exception e) when (e is CryptographicException or FormatException) {
            // A malformed key or signature is a bad signature.
        }

        throw new RegistryException($"the signature on {entry.Name} {entry.Version} does not verify: the registry has been tampered with");
    }

    public static string Checksum(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>The publishing key at <paramref name="path" />, made on first use (ECDSA P-256, PKCS#8 PEM, readable by its owner only).</summary>
    public static ECDsa LoadOrCreateKey(string path, TextWriter? log = null) {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path)) {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        log?.WriteLine($"    Created a publishing key at {path}");
        return key;
    }

    // ---- Archives ----------------------------------------------------------------------------------

    static readonly DateTimeOffset ArchiveTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     A deterministic archive of files (relative path → bytes): an
    ///     uncompressed ustar, entries sorted, every time, owner and mode the
    ///     same — so the same files always hash the same.
    /// </summary>
    public static byte[] Pack(IReadOnlyDictionary<string, byte[]> files) {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true)) {
            foreach (var (path, bytes) in files.OrderBy(static f => f.Key, StringComparer.Ordinal)) {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, path) {
                    ModificationTime = ArchiveTime,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    Uid = 0,
                    Gid = 0,
                    UserName = "",
                    GroupName = "",
                    DataStream = new MemoryStream(bytes),
                };
                writer.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    /// <summary>The files of an archive, refusing any path that would escape the directory it is unpacked into.</summary>
    public static IReadOnlyDictionary<string, byte[]> Unpack(byte[] archive) {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using var reader = new TarReader(new MemoryStream(archive));
        while (reader.GetNextEntry() is { } entry) {
            if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile) {
                throw new RegistryException($"the archive holds '{entry.Name}', which is not a regular file");
            }

            var name = entry.Name;
            if (Path.IsPathRooted(name) || name.Split('/').Any(static s => s is ".." or "")) {
                throw new RegistryException($"the archive holds '{name}', a path outside the package");
            }

            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data);
            files[name] = data.ToArray();
        }

        return files;
    }
}
