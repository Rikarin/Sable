using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Sable.Driver.Packages;

/// <summary>
///     A registry served over HTTP (docs/05 §5.5): the client side of the
///     protocol <see cref="RegistryServer" /> serves.
/// </summary>
/// <remarks>
///     <para>
///         Reading needs no account: <c>GET config.json</c>,
///         <c>GET index/&lt;name&gt;.json</c> (the same index a directory
///         registry keeps) and <c>GET blobs/&lt;sha256&gt;.tar</c>, and
///         <c>GET api/v1/search?q=</c>. Every index entry is checked here
///         against its owners' signatures and every archive against its
///         checksum, so the server is trusted to keep packages, not to vouch
///         for them.
///     </para>
///     <para>
///         Writing takes a token (<c>sable login</c>), sent as
///         <c>Authorization: Bearer</c>, and is signed besides with the
///         publisher's key: <c>PUT api/v1/packages</c> with the signed entry
///         and the archive; <c>POST api/v1/yank</c> and
///         <c>POST api/v1/owners</c> with a signature over a text naming the
///         state they change (<see cref="RegistryRequests" />). The token says
///         who may write to the server; the key says who the package's owner is.
///     </para>
/// </remarks>
public sealed class HttpRegistry : IRegistry, IDisposable {
    readonly HttpClient _http;
    readonly string? _token;
    readonly Dictionary<string, PackageIndex?> _indexes = new(StringComparer.Ordinal);
    bool? _available;

    /// <param name="url">The registry's root, <c>https://…/</c>.</param>
    /// <param name="token">What <c>sable login</c> stored for it, if anything.</param>
    public HttpRegistry(string url, string? token, bool offline = false, HttpMessageHandler? handler = null) {
        Location = url.EndsWith('/') ? url : url + "/";
        _token = token;
        Offline = offline;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(Location);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sable", Toolchains.RunningVersion));
    }

    /// <summary>Whether <paramref name="location" /> names a network registry rather than a directory.</summary>
    public static bool IsUrl(string location) =>
        location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public string Location { get; }
    public bool Offline { get; }

    public bool IsAvailable => !Offline && (_available ??= Probe());

    bool Probe() {
        try {
            using var response = Send(HttpMethod.Get, "config.json", null, authorise: false);
            return response.IsSuccessStatusCode;
        } catch (RegistryException) {
            return false;
        }
    }

    public PackageIndex? Index(string name) {
        if (_indexes.TryGetValue(name, out var known)) {
            return known;
        }

        using var response = Send(HttpMethod.Get, $"index/{PathOf(name)}.json", null, authorise: false);
        if (response.StatusCode == HttpStatusCode.NotFound) {
            return _indexes[name] = null;
        }

        return _indexes[name] = PackageIndex.Parse(name, Body(response));
    }

    public IReadOnlyList<IndexEntry> Entries(string name) => Index(name)?.Versions ?? [];

    public IndexEntry? Entry(string name, SemVersion version) => Entries(name).FirstOrDefault(e => e.Version == version);

    public IReadOnlyList<string> Owners(string name) => Index(name)?.Owners ?? [];

    public IndexEntry Publish(IndexEntry unsigned, byte[] archive, ECDsa key) {
        var entry = Registry.Sign(unsigned, key);
        var body = new JsonObject {
            ["name"] = entry.Name,
            ["entry"] = entry.Write(),
            ["archive"] = Convert.ToBase64String(archive),
        };
        using var response = Send(HttpMethod.Put, "api/v1/packages", body, authorise: true);
        Body(response);
        _indexes.Remove(entry.Name);
        return entry;
    }

    public void Yank(string name, SemVersion version, ECDsa key, bool yanked = true) {
        var current = Index(name)?.Versions.FirstOrDefault(e => e.Version == version) ?? throw new RegistryException($"{name} {version} is not published");
        var body = new JsonObject {
            ["name"] = name,
            ["version"] = version.ToString(),
            ["yanked"] = yanked,
            ["key"] = Registry.PublicKeyOf(key),
            ["signature"] = Registry.SignText(key, RegistryRequests.Yank(name, version, yanked, current.Yanked)),
        };
        using var response = Send(HttpMethod.Post, "api/v1/yank", body, authorise: true);
        Body(response);
        _indexes.Remove(name);
    }

    public void ChangeOwner(string name, string owner, bool add, ECDsa key) {
        var index = Index(name) ?? throw new RegistryException($"the registry has no package called '{name}'");
        var action = add ? "add" : "remove";
        var body = new JsonObject {
            ["name"] = name,
            ["action"] = action,
            ["owner"] = owner,
            ["key"] = Registry.PublicKeyOf(key),
            ["signature"] = Registry.SignText(key, RegistryRequests.OwnerChange(name, action, owner, index.Owners)),
        };
        using var response = Send(HttpMethod.Post, "api/v1/owners", body, authorise: true);
        Body(response);
        _indexes.Remove(name);
    }

    public byte[] Download(IndexEntry entry) {
        var index = Index(entry.Name) ?? throw new RegistryException($"the registry has no package called '{entry.Name}'");
        index.Verify(entry);
        using var response = Send(HttpMethod.Get, $"blobs/{entry.Checksum.Replace("sha256:", "", StringComparison.Ordinal)}.tar", null, authorise: false);
        if (response.StatusCode == HttpStatusCode.NotFound) {
            throw new RegistryException($"the registry has no archive for {entry.Name} {entry.Version}");
        }

        Check(response);
        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (Registry.Checksum(bytes) != entry.Checksum) {
            throw new RegistryException($"the archive of {entry.Name} {entry.Version} does not match its checksum: the registry has been tampered with");
        }

        return bytes;
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit = 20) {
        using var response = Send(HttpMethod.Get, $"api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}", null, authorise: false);
        var root = JsonNode.Parse(Body(response))!;
        return [
            ..root["packages"]!.AsArray().Select(static p => new SearchHit(
                p!["name"]!.GetValue<string>(),
                SemVersion.Parse(p["version"]!.GetValue<string>()),
                p["description"]?.GetValue<string>())),
        ];
    }

    /// <summary>Who the token is, as the registry knows it: what <c>sable login</c> checks before keeping it.</summary>
    public string WhoAmI() {
        using var response = Send(HttpMethod.Get, "api/v1/me", null, authorise: true);
        return JsonNode.Parse(Body(response))!["user"]!.GetValue<string>();
    }

    HttpResponseMessage Send(HttpMethod method, string path, JsonNode? body, bool authorise) {
        if (Offline) {
            throw new RegistryException($"--offline keeps the registry at '{Location}' from being read");
        }

        using var request = new HttpRequestMessage(method, path);
        if (authorise) {
            if (_token is null) {
                throw new RegistryException($"you are not logged in to {Location}: 'sable login --registry {Location}' takes a token from it");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        if (body is not null) {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        try {
            return _http.Send(request);
        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) {
            throw new RegistryException($"cannot reach the registry at {Location}: {e.Message}");
        }
    }

    /// <summary>The response's text, once it is known to have succeeded.</summary>
    static string Body(HttpResponseMessage response) {
        Check(response);
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    /// <summary>Turns a refusal into the registry's own reason.</summary>
    static void Check(HttpResponseMessage response) {
        if (response.IsSuccessStatusCode) {
            return;
        }

        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        string? reason = null;
        try {
            reason = JsonNode.Parse(text)?["error"]?.GetValue<string>();
        } catch (System.Text.Json.JsonException) {
            // Not the registry's JSON: a proxy's page, say.
        }

        throw new RegistryException(response.StatusCode == HttpStatusCode.Unauthorized
            ? $"the registry did not accept your token ({reason ?? "unauthorised"}): 'sable login' with a new one"
            : $"the registry refused: {reason ?? $"{(int)response.StatusCode} {response.ReasonPhrase}"}");
    }

    static string PathOf(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    public void Dispose() => _http.Dispose();
}
