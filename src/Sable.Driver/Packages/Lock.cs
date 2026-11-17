using System.Text;

namespace Sable.Driver.Packages;

/// <summary>One package a lock file pins.</summary>
/// <param name="Source">"registry", or "path+&lt;dir&gt;" relative to the workspace root.</param>
/// <param name="Checksum">For a registry package, the sha256 of its archive.</param>
/// <param name="Dependencies">The packages it depends on, as "name version".</param>
public sealed record LockedPackage(string Name, SemVersion Version, string Source, string? Checksum, IReadOnlyList<string> Dependencies) {
    public bool IsRegistry => Source == "registry";
}

/// <summary>
///     <c>sable.lock</c>: the exact version of every package a workspace
///     uses, and each registry package's checksum. Written deterministically
///     — sorted, nothing that depends on the machine or the time — so the
///     same resolution always writes the same bytes.
/// </summary>
public sealed class LockFile(IReadOnlyList<LockedPackage> packages) {
    public const int FormatVersion = 1;

    public IReadOnlyList<LockedPackage> Packages { get; } = [..packages.OrderBy(static p => p.Name, StringComparer.Ordinal).ThenBy(static p => p.Version)];

    public LockedPackage? Find(string name) => Packages.FirstOrDefault(p => p.Name == name);

    public string Write() {
        var builder = new StringBuilder();
        builder.Append("# sable.lock: written by sable, to be read by sable. Do not edit it by hand.\n");
        builder.Append($"version = {FormatVersion}\n");
        foreach (var package in Packages) {
            builder.Append("\n[[package]]\n");
            builder.Append($"name = {TomlText.Quote(package.Name)}\n");
            builder.Append($"version = {TomlText.Quote(package.Version.ToString())}\n");
            builder.Append($"source = {TomlText.Quote(package.Source)}\n");
            if (package.Checksum is { } checksum) {
                builder.Append($"checksum = {TomlText.Quote(checksum)}\n");
            }

            if (package.Dependencies.Count > 0) {
                builder.Append("dependencies = [\n");
                foreach (var dependency in package.Dependencies.Order(StringComparer.Ordinal)) {
                    builder.Append($"    {TomlText.Quote(dependency)},\n");
                }

                builder.Append("]\n");
            }
        }

        return builder.ToString();
    }

    /// <exception cref="PackageException">The lock file cannot be read.</exception>
    public static LockFile Parse(string text, string file = Workspace.LockName) {
        try {
            var root = TomlReader.Parse(text, file);
            if (root["version"] is not TomlInteger { Value: FormatVersion }) {
                throw new PackageException($"{file}: a lock file of a format this sable does not read; delete it and build again");
            }

            var packages = new List<LockedPackage>();
            if (root["package"] is TomlArray { IsArrayOfTables: true } array) {
                foreach (var table in array.Items.Cast<TomlTable>()) {
                    string Get(string key) => table[key] is TomlString s ? s.Value : throw new PackageException($"{file}:{table.Position.Line}: a locked package needs '{key}'");
                    packages.Add(new(
                        Get("name"),
                        SemVersion.Parse(Get("version")),
                        Get("source"),
                        table["checksum"] is TomlString c ? c.Value : null,
                        table["dependencies"] is TomlArray d ? [..d.Items.OfType<TomlString>().Select(static s => s.Value)] : []));
                }
            }

            return new(packages);
        } catch (TomlException e) {
            throw new PackageException($"the lock file is damaged: {e.Message}");
        } catch (FormatException e) {
            throw new PackageException($"{file}: the lock file is damaged: {e.Message}");
        }
    }

    public static LockFile? Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path), path) : null;
}
