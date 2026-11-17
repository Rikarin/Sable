using System.Diagnostics;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A temporary directory for package tests, with its own registry, cache
///     and key under <c>home/</c> — never the real <c>~/.sable</c>.
/// </summary>
internal sealed class PackageFixture : IDisposable {
    public PackageFixture() {
        Root = Directory.CreateTempSubdirectory("sable-packages-").FullName;
        Output = new StringWriter();
        Errors = new StringWriter();
        Environment = PackageEnvironment.At(Path.Combine(Root, "home"), Output, Errors);
    }

    public string Root { get; }
    public StringWriter Output { get; }
    public StringWriter Errors { get; }
    public PackageEnvironment Environment { get; }

    public string Home => Path.Combine(Root, "home");

    /// <summary>Writes a file, making its directory; the text loses the indentation a raw string gave it.</summary>
    public string Write(string relative, string text) {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n");
        return path;
    }

    /// <summary>A package: <c>sable.toml</c> with [package] and the rest given, and its files.</summary>
    public string Package(string directory, string name, string version, string rest = "", params (string Path, string Text)[] files) {
        Write($"{directory}/sable.toml", $"[package]\nname = \"{name}\"\nversion = \"{version}\"\n{rest}");
        foreach (var (path, text) in files) {
            Write($"{directory}/{path}", text);
        }

        return Path.Combine(Root, directory);
    }

    public Workspace Load(string directory) => Workspace.Load(Path.Combine(Root, directory));

    /// <summary>Publishes the package in <paramref name="directory" /> to this fixture's registry.</summary>
    public void Publish(string directory) {
        var workspace = Load(directory);
        PackageOperations.Publish(workspace, Environment, workspace.Members[0], dryRun: false, allowDirty: true);
    }

    /// <summary>A library package with one public function returning <paramref name="value" />, published.</summary>
    public void PublishLibrary(string name, string version, string dependencies = "", string value = "0") {
        var module = Scaffold.ModuleName(name);
        var directory = $"published/{name}-{version}";
        Package(directory, name, version, $"[dependencies]\n{dependencies}",
            ("src/lib.sab", $"module {module}\n\npublic fn {module.ToLowerInvariant()}Value() -> Int {{\n    return {value}\n}}\n"));
        Publish(directory);
    }

    /// <summary>Runs an executable, returning what it printed and its exit code.</summary>
    public static (string Output, int ExitCode) Execute(string executable, params string[] arguments) {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output.Result, process.ExitCode);
    }

    public void Dispose() {
        try {
            Directory.Delete(Root, true);
        } catch (IOException) {
            // A test's leftovers in the temporary directory are harmless.
        }
    }
}
