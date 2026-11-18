using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.Driver.Packages;

/// <summary>Someone the registry server lets write: by name, and the namespaces they may publish under.</summary>
/// <param name="Organisations">The <c>org/</c> prefixes they may publish under, besides their own name.</param>
/// <param name="Reserved">Whether they may publish an unprefixed name: the standard ecosystem's (docs/05 §5.5).</param>
public sealed record RegistryUser(string Name, IReadOnlyList<string> Organisations, bool Reserved);

/// <summary>
///     The registry server's tokens, in <c>tokens.json</c> beside its packages:
///     each token's SHA-256 (never the token) and whom it stands for.
/// </summary>
public static class RegistryTokens {
    public static string PathIn(string root) => Path.Combine(root, "tokens.json");

    /// <summary>Makes a token for <paramref name="user" />, records its hash, and returns it: the only time it is seen.</summary>
    public static string Issue(string root, RegistryUser user) {
        if (!Manifest.IsValidPackageName(user.Name) || user.Name.Contains('/')) {
            throw new PackageException($"'{user.Name}' is not a user name: lower-case letters, digits, '-' and '_'");
        }

        var token = "sbl_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var all = Load(root);
        all.Add(new JsonObject {
            ["sha256"] = Hash(token),
            ["user"] = user.Name,
            ["orgs"] = new JsonArray([..user.Organisations.Select(static o => (JsonNode)JsonValue.Create(o)!)]),
            ["reserved"] = user.Reserved,
        });
        Directory.CreateDirectory(root);
        var path = PathIn(root);
        File.WriteAllText(path, new JsonObject { ["tokens"] = all }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return token;
    }

    /// <summary>Whom a token stands for; null for one the server never issued.</summary>
    public static RegistryUser? Find(string root, string token) {
        var hash = Hash(token);
        var found = Load(root).FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t!["sha256"]!.GetValue<string>()), Encoding.ASCII.GetBytes(hash)));
        return found is null
            ? null
            : new(found["user"]!.GetValue<string>(), [..found["orgs"]?.AsArray().Select(static o => o!.GetValue<string>()) ?? []], found["reserved"]?.GetValue<bool>() ?? false);
    }

    static JsonArray Load(string root) =>
        File.Exists(PathIn(root)) ? JsonNode.Parse(File.ReadAllText(PathIn(root)))!["tokens"]!.AsArray().DeepClone().AsArray() : [];

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
///     A registry served over HTTP (docs/05 §5.5): the protocol
///     <see cref="HttpRegistry" /> speaks, over a directory registry
///     (<see cref="Registry" />) under <paramref name="root" />. It is the
///     reference implementation — what <c>sable registry serve</c> runs, and
///     what the tests publish to — not a hardened public service: put it
///     behind a proxy that terminates TLS and limits request sizes, and back
///     <c>root</c> up.
/// </summary>
/// <remarks>
///     <para>
///         It checks what it is given as a client would, and more: the token
///         (who may write here), the namespace (an <c>org/</c> prefix the token
///         holds, an unprefixed name only for a token of the standard
///         ecosystem), the owner's signature, the checksum, and that the
///         archive's <c>sable.toml</c> is the package and version its entry
///         claims.
///     </para>
/// </remarks>
public sealed class RegistryServer(string root, string prefix) : IDisposable {
    /// <summary>The largest archive accepted: a registry is for source.</summary>
    const int MaximumArchive = 32 * 1024 * 1024;

    readonly Registry _store = new(Path.Combine(root, "store"));
    readonly HttpListener _listener = new() { Prefixes = { prefix } };
    readonly Lock _writing = new();
    Task? _loop;

    /// <summary>Where it serves, as given: <c>http://127.0.0.1:8742/</c>.</summary>
    public string Prefix => prefix;

    /// <summary>Requests it served, and how each ended, for a log: <c>PUT /api/v1/packages 200</c>.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;

    public void Start() {
        Directory.CreateDirectory(Path.Combine(root, "store", "index"));
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    async Task Serve() {
        while (_listener.IsListening) {
            HttpListenerContext context;
            try {
                context = await _listener.GetContextAsync();
            } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) {
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    /// <summary>Serves until the process is stopped.</summary>
    public void Wait() => _loop?.GetAwaiter().GetResult();

    void Handle(HttpListenerContext context) {
        var request = context.Request;
        var response = context.Response;
        var status = 200;
        try {
            var path = Uri.UnescapeDataString(request.Url!.AbsolutePath);
            var basePath = new Uri(prefix).AbsolutePath;
            path = path.StartsWith(basePath, StringComparison.Ordinal) ? path[basePath.Length..] : path.TrimStart('/');
            (status, var body, var type) = Route(request.HttpMethod, path, request);
            Respond(response, status, body, type);
        } catch (RegistryException e) {
            Respond(response, status = 400, Error(e.Message), "application/json");
        } catch (Refusal e) {
            Respond(response, status = e.Status, Error(e.Message), "application/json");
        } catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException or KeyNotFoundException) {
            Respond(response, status = 400, Error($"a malformed request: {e.Message}"), "application/json");
        } catch (Exception e) when (e is HttpListenerException or IOException) {
            // The client went away.
        } finally {
            Log.WriteLine($"{request.HttpMethod} {request.Url?.PathAndQuery} {status}");
        }
    }

    sealed class Refusal(int status, string message) : Exception(message) {
        public int Status { get; } = status;
    }

    (int Status, byte[] Body, string Type) Route(string method, string path, HttpListenerRequest request) {
        static (int, byte[], string) Json(JsonNode node) => (200, Encoding.UTF8.GetBytes(node.ToJsonString()), "application/json");

        switch (method, path) {
            case ("GET", "config.json"):
                return Json(new JsonObject { ["api"] = "v1", ["registry"] = "sable" });

            case ("GET", _) when path.StartsWith("index/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal): {
                var name = path["index/".Length..^".json".Length];
                if (!Manifest.IsValidPackageName(name)) {
                    throw new Refusal(404, $"no package called '{name}'");
                }

                return _store.IndexText(name) is { } text ? (200, Encoding.UTF8.GetBytes(text), "application/json") : throw new Refusal(404, $"no package called '{name}'");
            }

            case ("GET", _) when path.StartsWith("blobs/", StringComparison.Ordinal) && path.EndsWith(".tar", StringComparison.Ordinal): {
                var hex = path["blobs/".Length..^".tar".Length];
                if (hex.Length != 64 || !hex.All(char.IsAsciiHexDigitLower)) {
                    throw new Refusal(404, "no such archive");
                }

                return _store.Blob("sha256:" + hex) is { } bytes ? (200, bytes, "application/x-tar") : throw new Refusal(404, "no such archive");
            }

            case ("GET", "api/v1/search"): {
                var limit = int.TryParse(request.QueryString["limit"], out var asked) ? Math.Clamp(asked, 1, 100) : 20;
                var hits = _store.Search(request.QueryString["q"] ?? "", limit);
                return Json(new JsonObject {
                    ["packages"] = new JsonArray([..hits.Select(static h => (JsonNode)new JsonObject { ["name"] = h.Name, ["version"] = h.Version.ToString(), ["description"] = h.Description })]),
                });
            }

            case ("GET", "api/v1/me"):
                return Json(new JsonObject { ["user"] = User(request).Name });

            case ("PUT", "api/v1/packages"): {
                var user = User(request);
                var body = ReadBody(request);
                var name = body["name"]!.GetValue<string>();
                CheckNamespace(user, name);
                var entry = IndexEntry.Read(name, body["entry"]!.AsObject());
                var archive = Convert.FromBase64String(body["archive"]!.GetValue<string>());
                CheckArchive(entry, archive);
                lock (_writing) {
                    _store.PublishSigned(entry, archive);
                }

                return Json(new JsonObject { ["published"] = $"{entry.Name} {entry.Version}" });
            }

            case ("POST", "api/v1/yank"): {
                User(request);
                var body = ReadBody(request);
                lock (_writing) {
                    _store.YankSigned(body["name"]!.GetValue<string>(), SemVersion.Parse(body["version"]!.GetValue<string>()), body["yanked"]!.GetValue<bool>(),
                        body["key"]!.GetValue<string>(), body["signature"]!.GetValue<string>());
                }

                return Json(new JsonObject { ["ok"] = true });
            }

            case ("POST", "api/v1/owners"): {
                User(request);
                var body = ReadBody(request);
                var action = body["action"]!.GetValue<string>();
                if (action is not ("add" or "remove")) {
                    throw new Refusal(400, $"'{action}' is not add or remove");
                }

                lock (_writing) {
                    _store.ChangeOwnerSigned(body["name"]!.GetValue<string>(), body["owner"]!.GetValue<string>(), action == "add",
                        body["key"]!.GetValue<string>(), body["signature"]!.GetValue<string>());
                }

                return Json(new JsonObject { ["ok"] = true });
            }

            default:
                throw new Refusal(404, $"no such endpoint: {method} /{path}");
        }
    }

    /// <summary>Whom the request's token stands for.</summary>
    RegistryUser User(HttpListenerRequest request) {
        var header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) {
            throw new Refusal(401, "this needs a token: 'sable login'");
        }

        return RegistryTokens.Find(root, header["Bearer ".Length..].Trim()) ?? throw new Refusal(401, "an unknown token");
    }

    /// <summary>
    ///     Namespacing (docs/05 §5.5): <c>org/package</c> is published by the
    ///     user called <c>org</c> or by members of it; an unprefixed name is
    ///     reserved for the standard ecosystem.
    /// </summary>
    static void CheckNamespace(RegistryUser user, string name) {
        if (!Manifest.IsValidPackageName(name)) {
            throw new Refusal(400, $"'{name}' is not a package name");
        }

        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) {
            if (!user.Reserved) {
                throw new Refusal(403, $"'{name}' has no namespace, and names without one are reserved for the standard ecosystem (docs/05 §5.5): publish it as '{user.Name}/{name}'");
            }

            return;
        }

        var organisation = name[..slash];
        if (organisation != user.Name && !user.Organisations.Contains(organisation)) {
            throw new Refusal(403, $"'{user.Name}' may not publish under '{organisation}/': only as '{user.Name}/…'{(user.Organisations.Count > 0 ? $" or under {string.Join(", ", user.Organisations.Select(static o => o + "/"))}" : "")}");
        }
    }

    /// <summary>That the archive is the package its entry says, by the manifest inside it.</summary>
    static void CheckArchive(IndexEntry entry, byte[] archive) {
        if (archive.Length > MaximumArchive) {
            throw new Refusal(413, $"the archive is {archive.Length:N0} bytes; the registry takes at most {MaximumArchive:N0}");
        }

        var files = Registry.Unpack(archive);
        if (!files.TryGetValue(ManifestFiles.TomlFile, out var toml)) {
            throw new Refusal(400, $"the archive has no {ManifestFiles.TomlFile}: a published package carries its evaluated manifest (docs/05 §5.2.6)");
        }

        Manifest manifest;
        try {
            manifest = Manifest.Parse(Encoding.UTF8.GetString(toml));
        } catch (ManifestException e) {
            throw new Refusal(400, $"the archive's {ManifestFiles.TomlFile} is not valid: {e.Message}");
        }

        if (manifest.Package?.Name != entry.Name || manifest.Package.Version != entry.Version) {
            throw new Refusal(400, $"the archive holds {manifest.Package?.Name} {manifest.Package?.Version}, not {entry.Name} {entry.Version}");
        }
    }

    static JsonObject ReadBody(HttpListenerRequest request) {
        if (request.ContentLength64 > MaximumArchive * 2L) {
            throw new Refusal(413, "the request is too large");
        }

        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    static byte[] Error(string message) => Encoding.UTF8.GetBytes(new JsonObject { ["error"] = message }.ToJsonString());

    static void Respond(HttpListenerResponse response, int status, byte[] body, string type) {
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
        response.Close();
    }

    public void Dispose() {
        if (_listener.IsListening) {
            _listener.Stop();
        }

        _listener.Close();
        try {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        } catch (AggregateException) {
            // It stopped.
        }
    }
}
