using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.Driver.Packages;

/// <summary>
///     The tokens <c>sable login</c> keeps, one a registry, in
///     <c>$SABLE_HOME/credentials.json</c>, readable by its owner only.
/// </summary>
public static class Credentials {
    public static string PathIn(string home) => Path.Combine(home, "credentials.json");

    /// <summary>The token for the registry at <paramref name="url" />, if there is one.</summary>
    public static string? Token(string home, string url) => Load(home)[Key(url)]?.GetValue<string>();

    public static void Save(string home, string url, string token) {
        var all = Load(home);
        all[Key(url)] = token;
        Write(home, all);
    }

    /// <summary>Forgets the token for <paramref name="url" />; whether there was one.</summary>
    public static bool Remove(string home, string url) {
        var all = Load(home);
        if (!all.Remove(Key(url))) {
            return false;
        }

        Write(home, all);
        return true;
    }

    static string Key(string url) => url.EndsWith('/') ? url : url + "/";

    static JsonObject Load(string home) {
        var path = PathIn(home);
        if (!File.Exists(path)) {
            return [];
        }

        try {
            return JsonNode.Parse(File.ReadAllText(path))?["registries"]?.AsObject().DeepClone().AsObject() ?? [];
        } catch (JsonException) {
            throw new PackageException($"{path} is damaged; delete it and 'sable login' again");
        }
    }

    static void Write(string home, JsonObject registries) {
        Directory.CreateDirectory(home);
        var path = PathIn(home);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, "");
        if (!OperatingSystem.IsWindows()) {
            // Readable by its owner only, before the tokens are in it.
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(temporary, new JsonObject { ["registries"] = registries }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temporary, path, true);
    }
}
