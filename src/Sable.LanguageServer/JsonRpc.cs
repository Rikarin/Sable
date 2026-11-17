using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.LanguageServer;

/// <summary>
///     JSON-RPC 2.0 over a pair of streams, framed as the Language Server
///     Protocol frames it: a <c>Content-Length</c> header, a blank line, and
///     that many bytes of UTF-8 JSON.
/// </summary>
/// <remarks>
///     Reading is one loop's job, so it is not synchronised; writing happens
///     from whichever request finishes, so it is, one message at a time.
/// </remarks>
internal sealed class JsonRpcChannel(Stream input, Stream output) {
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly byte[] _one = new byte[1];

    /// <summary>The next message, or null at the end of the input.</summary>
    public async Task<JsonElement?> ReadAsync(CancellationToken cancellation) {
        var length = -1;
        while (true) {
            var line = await ReadLineAsync(cancellation);
            if (line is null) {
                return null;
            }

            if (line.Length == 0) {
                if (length >= 0) {
                    break;
                }

                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) {
                length = int.Parse(line[(colon + 1)..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        var body = new byte[length];
        var read = 0;
        while (read < length) {
            var n = await input.ReadAsync(body.AsMemory(read, length - read), cancellation);
            if (n == 0) {
                return null;
            }

            read += n;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    async Task<string?> ReadLineAsync(CancellationToken cancellation) {
        var bytes = new List<byte>();
        while (true) {
            var n = await input.ReadAsync(_one.AsMemory(0, 1), cancellation);
            if (n == 0) {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString([..bytes]);
            }

            if (_one[0] == (byte)'\n') {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r') {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.ASCII.GetString([..bytes]);
            }

            bytes.Add(_one[0]);
        }
    }

    /// <summary>Writes one message, whole, before any other.</summary>
    public async Task WriteAsync(JsonNode message) {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(Json.Options));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try {
            await output.WriteAsync(header);
            await output.WriteAsync(body);
            await output.FlushAsync();
        } finally {
            _writeLock.Release();
        }
    }
}

/// <summary>The serialiser settings every message is written with.</summary>
internal static class Json {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>A property of an object, if it is there and not null.</summary>
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? GetString(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static int? GetInt(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : null;

    public static bool GetBool(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.True };

    /// <summary>Any value as a node: what <see cref="JsonSerializer" /> makes of it.</summary>
    public static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Options);
}

/// <summary>A request that failed in a way the protocol has a code for.</summary>
internal sealed class LspException(int code, string message) : Exception(message) {
    public int Code { get; } = code;

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerNotInitialized = -32002;
    public const int RequestFailed = -32803;
    public const int RequestCancelled = -32800;
}
