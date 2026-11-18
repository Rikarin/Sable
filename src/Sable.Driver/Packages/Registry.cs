using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.Driver.Packages;

/// <summary>A dependency as the registry's index records it: what a resolver needs without the archive.</summary>
public sealed record IndexDependency(string Name, string Requirement, string Kind, bool Optional, bool DefaultFeatures, IReadOnlyList<string> Features);

/// <summary>One published version of a package.</summary>
/// <param name="Description">The manifest's description, for <c>sable search</c>: shown, not signed.</param>
public sealed record IndexEntry(
    string Name,
    SemVersion Version,
    string Checksum,
    IReadOnlyList<IndexDependency> Dependencies,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Features,
    bool Yanked,
    string PublicKey,
    string Signature,
    string? Description = null) {
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

    public static IndexEntry Read(string name, JsonObject v) => new(
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
        v["signature"]!.GetValue<string>(),
        v["description"]?.GetValue<string>());

    public JsonObject Write() {
        var json = new JsonObject {
            ["version"] = Version.ToString(),
            ["checksum"] = Checksum,
            ["dependencies"] = new JsonArray([..Dependencies.Select(static d => (JsonNode)new JsonObject {
                ["name"] = d.Name,
                ["req"] = d.Requirement,
                ["kind"] = d.Kind,
                ["optional"] = d.Optional,
                ["default-features"] = d.DefaultFeatures,
                ["features"] = new JsonArray([..d.Features.Select(static f => (JsonNode)JsonValue.Create(f)!)]),
            })]),
            ["features"] = new JsonObject(Features.OrderBy(static f => f.Key, StringComparer.Ordinal).Select(static f =>
                KeyValuePair.Create(f.Key, (JsonNode?)new JsonArray([..f.Value.Select(static x => (JsonNode)JsonValue.Create(x)!)])))),
            ["yanked"] = Yanked,
            ["key"] = PublicKey,
            ["signature"] = Signature,
        };
        if (Description is not null) {
            json["description"] = Description;
        }

        return json;
    }
}

/// <summary>Something is wrong with a registry, a package in it, or the cache: a checksum, a signature, a version that exists.</summary>
public sealed class RegistryException(string message) : Exception(message);

/// <summary>A package <c>sable search</c> found: its name, its newest version, and what it says it is.</summary>
public sealed record SearchHit(string Name, SemVersion Version, string? Description);

/// <summary>
///     A package's index: who may publish it, the keys that ever could, and
///     its versions — the same JSON in a directory registry's
///     <c>index/&lt;name&gt;.json</c> and at a network registry's
///     <c>/index/&lt;name&gt;.json</c>.
/// </summary>
/// <param name="Owners">The keys that may publish, yank and change the owners now; the first publisher's first.</param>
/// <param name="Keys">Every key that has been an owner: a version is verified against these, so removing an owner does not unpublish what it signed.</param>
public sealed record PackageIndex(string Name, IReadOnlyList<string> Owners, IReadOnlyList<string> Keys, IReadOnlyList<IndexEntry> Versions) {
    /// <exception cref="RegistryException">It is not an index.</exception>
    public static PackageIndex Parse(string name, string json) {
        try {
            // A key named twice is refused as the text is read: left to the
            // node, it would throw an ArgumentException at the first lookup.
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false })!.AsObject();
            var owner = root["owner"]?.GetValue<string>();
            IReadOnlyList<string> owners = root["owners"] is JsonArray list ? [..list.Select(static o => o!.GetValue<string>())] : owner is null ? [] : [owner];
            IReadOnlyList<string> keys = root["keys"] is JsonArray all ? [..all.Select(static o => o!.GetValue<string>())] : owners;
            return new(name, owners, keys, [..root["versions"]!.AsArray().Select(v => IndexEntry.Read(name, v!.AsObject()))]);
        } catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException or KeyNotFoundException) {
            throw new RegistryException($"the registry's index for '{name}' is damaged: {e.Message}");
        }
    }

    public string Write() {
        var root = new JsonObject {
            ["name"] = Name,
            // The first owner, as the index had it before there could be several.
            ["owner"] = Owners.Count > 0 ? Owners[0] : null,
            ["owners"] = new JsonArray([..Owners.Select(static o => (JsonNode)JsonValue.Create(o)!)]),
            ["keys"] = new JsonArray([..Keys.Select(static o => (JsonNode)JsonValue.Create(o)!)]),
            ["versions"] = new JsonArray([..Versions.Select(static e => (JsonNode)e.Write())]),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>Checks a version's signature, and that the key that made it is, or was, an owner's.</summary>
    /// <exception cref="RegistryException">It is not.</exception>
    public void Verify(IndexEntry entry) {
        if (!Keys.Contains(entry.PublicKey)) {
            throw new RegistryException($"{entry.Name} {entry.Version} is not signed by the package's owner");
        }

        if (!Registry.VerifySignature(entry.PublicKey, entry.SignedText(), entry.Signature)) {
            throw new RegistryException($"the signature on {entry.Name} {entry.Version} does not verify: the registry has been tampered with");
        }
    }
}

/// <summary>
///     What signs a change to a package's owners or a version's yanking: the
///     text an owner's key signs, which names the state it changes from, so a
///     request seen once cannot be replayed later to undo a change since.
/// </summary>
public static class RegistryRequests {
    public static string OwnerChange(string name, string action, string owner, IReadOnlyList<string> owners) =>
        $"sable-owner-v1\nname={name}\n{action}={owner}\nowners={string.Join(',', owners.Order(StringComparer.Ordinal))}\n";

    public static string Yank(string name, SemVersion version, bool yanked, bool wasYanked) =>
        $"sable-yank-v1\nname={name}\nversion={version}\nyanked={yanked}\nwas={wasYanked}\n";
}

/// <summary>
///     A registry (docs/05 §5.5), wherever it is: a directory
///     (<see cref="Registry" />) or a network service (<see cref="HttpRegistry" />).
///     Either way the index is signed by the packages' owners and checked by
///     whoever reads it, so a registry is trusted to keep what it is given,
///     not to vouch for it.
/// </summary>
public interface IRegistry {
    /// <summary>Where it is: a directory or a URL, for messages.</summary>
    string Location { get; }

    /// <summary><c>--offline</c>: it is not to be reached, though it may be there.</summary>
    bool Offline { get; }

    /// <summary>Whether it can be read now.</summary>
    bool IsAvailable { get; }

    /// <summary>Why it is not available, for a message: "the registry at '…' cannot be read".</summary>
    string WhyUnavailable => Offline ? $"--offline keeps the registry at '{Location}' from being read" : $"the registry at '{Location}' cannot be read";

    /// <summary>A package's index, null when nothing of that name is published.</summary>
    PackageIndex? Index(string name);

    /// <summary>Every version of <paramref name="name" /> published, yanked or not, oldest first.</summary>
    IReadOnlyList<IndexEntry> Entries(string name) => Index(name)?.Versions ?? [];

    IndexEntry? Entry(string name, SemVersion version) => Entries(name).FirstOrDefault(e => e.Version == version);

    /// <summary>The keys that own <paramref name="name" />.</summary>
    IReadOnlyList<string> Owners(string name) => Index(name)?.Owners ?? [];

    /// <summary>Adds a version, signed with <paramref name="key" />: its archive and its index entry.</summary>
    /// <exception cref="RegistryException">The version exists, the key is not an owner's, or the registry refused it.</exception>
    IndexEntry Publish(IndexEntry unsigned, byte[] archive, ECDsa key);

    /// <summary>Marks a version yanked (or not): no new resolution chooses it; lock files that hold it keep it.</summary>
    void Yank(string name, SemVersion version, ECDsa key, bool yanked = true);

    /// <summary>Adds an owner (a public key, as <see cref="Registry.PublicKeyOf" /> writes it) or, with <paramref name="add" /> false, removes one.</summary>
    void ChangeOwner(string name, string owner, bool add, ECDsa key);

    /// <summary>The archive of a version, checked against its signature and checksum.</summary>
    byte[] Download(IndexEntry entry);

    /// <summary>The packages whose names or descriptions hold <paramref name="query" />, best first.</summary>
    IReadOnlyList<SearchHit> Search(string query, int limit = 20);
}

/// <summary>
///     The registry as a directory (docs/05 §5.5), content-addressed and
///     append-only: <c>index/&lt;name&gt;.json</c> lists a package's versions,
///     each with its archive's sha256, its dependencies, and an ECDSA P-256
///     signature by an owner's key; <c>blobs/&lt;sha256&gt;.tar</c> holds the
///     archives. A version, once published, is never changed: yanking only
///     marks it unresolvable for new lock files. It is a private or local
///     registry, and the store behind <see cref="RegistryServer" />.
/// </summary>
public sealed class Registry(string directory) : IRegistry {
    public string Directory { get; } = directory;

    public string Location => Directory;

    string IndexPath(string name) => Path.Combine(Directory, "index", Path.Combine(name.Split('/')) + ".json");

    string BlobPath(string checksum) => Path.Combine(Directory, "blobs", checksum.Replace("sha256:", "", StringComparison.Ordinal) + ".tar");

    /// <summary><c>--offline</c>: the registry is not to be read, though it may be there.</summary>
    public bool Offline { get; init; }

    /// <summary>Whether the registry is there to read: offline, it may not be.</summary>
    public bool IsAvailable => !Offline && System.IO.Directory.Exists(Path.Combine(Directory, "index"));

    public PackageIndex? Index(string name) {
        var path = IndexPath(name);
        return File.Exists(path) ? PackageIndex.Parse(name, File.ReadAllText(path)) : null;
    }

    /// <summary>Every version of <paramref name="name" /> published, yanked or not, oldest first.</summary>
    public IReadOnlyList<IndexEntry> Entries(string name) => Index(name)?.Versions ?? [];

    /// <summary>The key that owns <paramref name="name" />: the first publisher's.</summary>
    public string? Owner(string name) => Index(name)?.Owners.FirstOrDefault();

    public IReadOnlyList<string> Owners(string name) => Index(name)?.Owners ?? [];

    public IndexEntry? Entry(string name, SemVersion version) => Entries(name).FirstOrDefault(e => e.Version == version);

    /// <summary>The raw JSON of an index, as a network registry serves it.</summary>
    public string? IndexText(string name) {
        var path = IndexPath(name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The bytes of the archive with this checksum, unchecked; null when there is none.</summary>
    public byte[]? Blob(string checksum) {
        var path = BlobPath(checksum);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    void WriteIndex(PackageIndex index) {
        var path = IndexPath(index.Name);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written whole to a temporary file and moved, so a reader never sees half of it.
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, index.Write());
        File.Move(temporary, path, true);
    }

    /// <summary>Adds a version: its archive and its signed index entry.</summary>
    /// <exception cref="RegistryException">The version exists already, or the key is not an owner's.</exception>
    public IndexEntry Publish(IndexEntry unsigned, byte[] archive, ECDsa key) => PublishSigned(Sign(unsigned, key), archive);

    /// <summary>An entry signed with <paramref name="key" />.</summary>
    public static IndexEntry Sign(IndexEntry unsigned, ECDsa key) {
        var entry = unsigned with { PublicKey = PublicKeyOf(key), Signature = "" };
        return entry with { Signature = SignText(key, entry.SignedText()) };
    }

    /// <summary>
    ///     Adds a version signed elsewhere — by the publisher, before it came
    ///     over the network — once its signature, its owner and its archive are checked.
    /// </summary>
    /// <exception cref="RegistryException">It does not check out.</exception>
    public IndexEntry PublishSigned(IndexEntry entry, byte[] archive) {
        var index = Index(entry.Name) ?? new PackageIndex(entry.Name, [entry.PublicKey], [entry.PublicKey], []);
        if (index.Versions.Any(e => e.Version == entry.Version)) {
            throw new RegistryException($"{entry.Name} {entry.Version} is already published, and a published version never changes: publish a new version");
        }

        if (!index.Owners.Contains(entry.PublicKey)) {
            throw new RegistryException($"{entry.Name} is owned by another key; only its owners may publish it");
        }

        if (!VerifySignature(entry.PublicKey, entry.SignedText(), entry.Signature)) {
            throw new RegistryException($"the signature on {entry.Name} {entry.Version} does not verify");
        }

        if (Checksum(archive) != entry.Checksum) {
            throw new RegistryException($"the archive of {entry.Name} {entry.Version} is not the one its entry describes");
        }

        var blob = BlobPath(entry.Checksum);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        if (!File.Exists(blob)) {
            File.WriteAllBytes(blob, archive);
        }

        WriteIndex(index with { Versions = [..index.Versions, entry with { Yanked = false }] });
        return entry;
    }

    /// <summary>Marks a version yanked (or not): no new resolution chooses it; lock files that hold it keep it.</summary>
    public void Yank(string name, SemVersion version, ECDsa key, bool yanked = true) {
        var index = Index(name);
        var current = index?.Versions.FirstOrDefault(e => e.Version == version) ?? throw new RegistryException($"{name} {version} is not published");
        YankSigned(name, version, yanked, PublicKeyOf(key), SignText(key, RegistryRequests.Yank(name, version, yanked, current.Yanked)));
    }

    /// <summary>A yank an owner signed: checked, then done.</summary>
    public void YankSigned(string name, SemVersion version, bool yanked, string signer, string signature) {
        var index = Index(name) ?? throw new RegistryException($"{name} {version} is not published");
        var position = index.Versions.ToList().FindIndex(e => e.Version == version);
        if (position < 0) {
            throw new RegistryException($"{name} {version} is not published");
        }

        if (!index.Owners.Contains(signer)) {
            throw new RegistryException($"{name} is owned by another key; only its owner may yank it");
        }

        if (!VerifySignature(signer, RegistryRequests.Yank(name, version, yanked, index.Versions[position].Yanked), signature)) {
            throw new RegistryException($"the request to yank {name} {version} is not signed by the key it names, or is stale");
        }

        var versions = index.Versions.ToList();
        versions[position] = versions[position] with { Yanked = yanked };
        WriteIndex(index with { Versions = versions });
    }

    public void ChangeOwner(string name, string owner, bool add, ECDsa key) {
        var index = Index(name) ?? throw new RegistryException($"the registry has no package called '{name}'");
        var action = add ? "add" : "remove";
        ChangeOwnerSigned(name, owner, add, PublicKeyOf(key), SignText(key, RegistryRequests.OwnerChange(name, action, owner, index.Owners)));
    }

    /// <summary>An owner change an owner signed: checked, then done.</summary>
    public void ChangeOwnerSigned(string name, string owner, bool add, string signer, string signature) {
        var index = Index(name) ?? throw new RegistryException($"the registry has no package called '{name}'");
        if (!index.Owners.Contains(signer)) {
            throw new RegistryException($"{name} is owned by other keys; only an owner may change its owners");
        }

        if (!VerifySignature(signer, RegistryRequests.OwnerChange(name, add ? "add" : "remove", owner, index.Owners), signature)) {
            throw new RegistryException($"the request to change the owners of {name} is not signed by the key it names, or is stale");
        }

        try {
            using var parsed = ECDsa.Create();
            parsed.ImportSubjectPublicKeyInfo(Convert.FromBase64String(owner), out _);
        } catch (Exception e) when (e is CryptographicException or FormatException) {
            throw new RegistryException($"'{owner}' is not a public key; 'sable owner --key' prints one");
        }

        if (add) {
            if (index.Owners.Contains(owner)) {
                return;
            }

            WriteIndex(index with { Owners = [..index.Owners, owner], Keys = index.Keys.Contains(owner) ? index.Keys : [..index.Keys, owner] });
            return;
        }

        if (!index.Owners.Contains(owner)) {
            throw new RegistryException($"that key is not an owner of {name}");
        }

        if (index.Owners.Count == 1) {
            throw new RegistryException($"that is the last owner of {name}: add another before removing it");
        }

        WriteIndex(index with { Owners = [..index.Owners.Where(o => o != owner)] });
    }

    /// <summary>
    ///     The archive of a version, checked: its signature is an owner's and
    ///     covers this entry, and its bytes hash to the checksum signed.
    /// </summary>
    public byte[] Download(IndexEntry entry) {
        Verify(entry);
        var bytes = Blob(entry.Checksum) ?? throw new RegistryException($"the registry has no archive for {entry.Name} {entry.Version}");
        if (Checksum(bytes) != entry.Checksum) {
            throw new RegistryException($"the archive of {entry.Name} {entry.Version} does not match its checksum: the registry has been tampered with");
        }

        return bytes;
    }

    /// <summary>Checks an entry's signature, and that the key that made it owns, or owned, the package.</summary>
    public void Verify(IndexEntry entry) {
        if (Index(entry.Name) is not { } index) {
            throw new RegistryException($"{entry.Name} {entry.Version} is not signed by the package's owner");
        }

        index.Verify(entry);
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit = 20) {
        var root = Path.Combine(Directory, "index");
        if (!System.IO.Directory.Exists(root)) {
            return [];
        }

        var hits = new List<(int Rank, SearchHit Hit)>();
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)) {
            var name = Path.GetRelativePath(root, file)[..^".json".Length].Replace(Path.DirectorySeparatorChar, '/');
            var versions = Entries(name).Where(static e => !e.Yanked).ToList();
            if (versions.Count == 0) {
                continue;
            }

            var latest = versions.Where(static e => !e.Version.IsPrerelease).DefaultIfEmpty(versions[^1]).MaxBy(static e => e.Version)!;
            if (SearchRank(name, latest.Description, query) is { } rank) {
                hits.Add((rank, new(name, latest.Version, latest.Description)));
            }
        }

        return [..hits.OrderBy(static h => h.Rank).ThenBy(static h => h.Hit.Name, StringComparer.Ordinal).Take(limit).Select(static h => h.Hit)];
    }

    /// <summary>How well a package matches a search, lower first: its name exactly, its name holding the words, then its description; null for no match.</summary>
    public static int? SearchRank(string name, string? description, string query) {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) {
            return 3;
        }

        var lowered = name.ToLowerInvariant();
        if (lowered == query.Trim().ToLowerInvariant()) {
            return 0;
        }

        if (words.All(lowered.Contains)) {
            return 1;
        }

        var text = $"{lowered} {description?.ToLowerInvariant()}";
        return words.All(text.Contains) ? 2 : null;
    }

    public static string Checksum(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static string SignText(ECDsa key, string text) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));

    /// <summary>Whether <paramref name="signature" /> is <paramref name="publicKey" />'s over <paramref name="text" />; a malformed key or signature is not.</summary>
    public static bool VerifySignature(string publicKey, string text, string signature) {
        try {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        } catch (Exception e) when (e is CryptographicException or FormatException) {
            return false;
        }
    }

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
