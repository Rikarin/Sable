using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Packages;

/// <summary>
///     Evaluates a manifest written in Sable — <c>package.sb</c> or
///     <c>workspace.sb</c> (docs/05 §5.2) — to canonical TOML, which
///     <see cref="Manifest" /> then reads as it reads <c>sable.toml</c>.
/// </summary>
/// <remarks>
///     <para>
///         The file is checked against the rules that keep a manifest data
///         (§5.2.4): it imports nothing but <c>Sable.Build</c>, has no
///         conditional compilation, and calls nothing that reads, writes or
///         waits. It is then compiled with <c>Sable.Build</c> and a
///         <c>main</c> that prints <c>renderManifest(manifest)</c>, and run in
///         the SIL interpreter under a step limit.
///     </para>
///     <para>
///         Results are cached by content: in memory for the process, and on
///         disk under <c>$SABLE_HOME/manifests</c>, keyed by the manifest's
///         text, <c>Sable.Build</c>'s, and the toolchain build.
///     </para>
/// </remarks>
public static class ManifestEvaluator {
    /// <summary>Enough for any manifest a person would write; a loop that never ends stops here.</summary>
    const int StepLimit = 20_000_000;

    static readonly ConcurrentDictionary<string, string> Memory = new(StringComparer.Ordinal);

    /// <summary>The value each kind of manifest declares, and its type.</summary>
    static (string Value, string Type) Declared(string path) =>
        Path.GetFileName(path) == ManifestFiles.WorkspaceFile ? ("workspace", "Workspace") : ("manifest", "Package");

    /// <summary>The manifest at <paramref name="path" />, evaluated to canonical TOML.</summary>
    /// <exception cref="ManifestException">It breaks a rule, does not type-check, or traps.</exception>
    public static string Evaluate(string path) {
        var text = File.ReadAllText(path);
        var key = Key(path, text);
        if (Memory.TryGetValue(key, out var known)) {
            return known;
        }

        var cached = Path.Combine(CacheDirectory, key + ".toml");
        if (File.Exists(cached)) {
            return Memory[key] = File.ReadAllText(cached);
        }

        var toml = EvaluateUncached(path, text);
        try {
            Directory.CreateDirectory(CacheDirectory);
            var temporary = cached + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, toml);
            File.Move(temporary, cached, overwrite: true);
        } catch (IOException) {
            // A cache that cannot be written is only slower.
        } catch (UnauthorizedAccessException) {
        }

        return Memory[key] = toml;
    }

    /// <summary>
    ///     Where evaluated manifests are kept: <c>$SABLE_HOME/manifests</c>
    ///     (default <c>~/.sable/manifests</c>). Tests point it elsewhere.
    /// </summary>
    public static string CacheDirectory {
        get => _cacheDirectory ?? Path.Combine(
            Environment.GetEnvironmentVariable("SABLE_HOME") is { Length: > 0 } home
                ? home
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sable"),
            "manifests");
        set => _cacheDirectory = value;
    }

    static string? _cacheDirectory;

    static string Key(string path, string text) {
        var library = Library.Source("Sable.Build");
        var input = $"{typeof(ManifestEvaluator).Assembly.ManifestModule.ModuleVersionId}\n{Path.GetFileName(path)}\n{library}\n{text}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    static string EvaluateUncached(string path, string text) {
        var (value, type) = Declared(path);
        var tree = SyntaxTree.Parse(new SourceText(text, path));
        var errors = new List<ManifestError>();

        void Report(int position, string key, string message) => errors.Add(new(path, tree.Text.GetLinePosition(position).Line, key, message));

        foreach (var diagnostic in tree.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)) {
            Report(diagnostic.Span.Start, diagnostic.Id, diagnostic.Message);
        }

        if (errors.Count > 0) {
            throw new ManifestException(errors);
        }

        CheckShape((CompilationUnitSyntax)tree.Root, tree, value, type, Report);
        if (errors.Count > 0) {
            throw new ManifestException(errors);
        }

        // Beside the manifest, so in its module (Compilation.IsManifestFile).
        var driverPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "<manifest>.sb");
        var driver = SyntaxTree.Parse(new SourceText($"import Sable.Build\n\nfn main() {{\n    print(renderManifest({value}))\n}}\n", driverPath));
        var program = new[] { driver, tree };
        var compilation = Compilation.Create([..program, ..Library.For(program)]);

        foreach (var (diagnostic, file) in compilation.GetAllDiagnosticsByFile().Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)) {
            if (file == tree || (file is null && diagnostic.Span.End <= tree.Text.Length && file != driver)) {
                Report(diagnostic.Span.Start, diagnostic.Id, diagnostic.Message);
            } else if (file == driver) {
                // The value is there (CheckShape saw it), so the driver fails only on its type.
                Report(DeclarationOf(tree, value), value, $"'{value}' must be a {type} (docs/05 §5.2)");
            }
        }

        if (errors.Count > 0) {
            throw new ManifestException([..errors.Distinct()]);
        }

        CheckEffects(compilation, tree, Report);
        if (errors.Count > 0) {
            throw new ManifestException(errors);
        }

        var (module, lowering) = SilGen.Lower(compilation);
        foreach (var diagnostic in lowering) {
            Report(diagnostic.Span.End <= tree.Text.Length ? diagnostic.Span.Start : 0, diagnostic.Id, diagnostic.Message);
        }

        if (errors.Count > 0) {
            throw new ManifestException(errors);
        }

        if (SilVerifier.Verify(module) is { Count: > 0 } problems) {
            throw new ManifestException([new(path, 1, "internal", $"SIL verification failed: {problems[0]}")]);
        }

        var interpreter = new SilInterpreter(module, StepLimit);
        try {
            interpreter.Run(module.EntryPoint!);
        } catch (SilTrap trap) {
            var reason = trap.Message.Contains("step limit", StringComparison.Ordinal)
                ? $"evaluating it took more than {StepLimit:N0} steps: a manifest is data, and should be quick to compute"
                : $"evaluating it trapped: {trap.Message}";
            throw new ManifestException([new(path, tree.Text.GetLinePosition(DeclarationOf(tree, value)).Line, value, reason)]);
        }

        return interpreter.Output.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    ///     What can be told from the syntax alone: no module, no imports but
    ///     <c>Sable.Build</c>, no conditional compilation, and the value
    ///     declared.
    /// </summary>
    static void CheckShape(CompilationUnitSyntax unit, SyntaxTree tree, string value, string type, Action<int, string, string> report) {
        if (unit.Module is { } module) {
            report(module.Span.Start, "module", "a manifest has no module declaration: it is evaluated on its own");
        }

        foreach (var import in unit.Imports) {
            var name = import.Name.ToString().Trim();
            if (name != "Sable.Build") {
                report(import.Span.Start, "import",
                    $"a manifest cannot import '{name}': it imports only Sable.Build, and may not read or write anything (docs/05 §5.2.4)");
            }
        }

        foreach (var token in tree.Root.DescendantTokens()) {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia)) {
                if (trivia.Kind is SyntaxKind.DirectiveTrivia or SyntaxKind.DisabledTextTrivia) {
                    // At the directive, not the token it is trivia of: where
                    // the branch is not taken, that is past its '#endif'.
                    report(trivia.Span.Start, "#if",
                        "a manifest has no conditional compilation: what depends on the platform is data the build applies, such as `when: .os(.linux)` on a dependency (docs/05 §5.2.4)");
                    return;
                }
            }
        }

        var declared = unit.Members.OfType<FieldDeclarationSyntax>().Any(f =>
            f.LetOrVarKeyword.Kind == SyntaxKind.LetKeyword && f.Declarator.Pattern is BindingPatternSyntax binding && binding.Identifier.Text == value);
        if (!declared) {
            report(0, value, $"a {Path.GetFileName(tree.Text.FileName)} declares `let {value} = {type} {{ ... }}` (docs/05 §5.2)");
        }
    }

    /// <summary>Where <c>let value</c> is declared: what an error about the value as a whole points at.</summary>
    static int DeclarationOf(SyntaxTree tree, string value) =>
        ((CompilationUnitSyntax)tree.Root).Members.OfType<FieldDeclarationSyntax>()
        .FirstOrDefault(f => f.Declarator.Pattern is BindingPatternSyntax binding && binding.Identifier.Text == value)?.Span.Start ?? 0;

    /// <summary>
    ///     Nothing the manifest names may read, write or wait: the core's
    ///     <c>Runtime</c>, <c>print</c>, tasks.
    /// </summary>
    static void CheckEffects(Compilation compilation, SyntaxTree tree, Action<int, string, string> report) {
        var model = compilation.GetSemanticModel(tree);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tree.Root.DescendantNodes()) {
            if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax) || model.GetSymbolOf(node) is not { } symbol || Effect(symbol) is not { } effect) {
                continue;
            }

            if (reported.Add($"{node.Span.Start}")) {
                report(node.Span.Start, "effect",
                    $"a manifest cannot use '{effect}': it may not read, write or wait, so that it means the same on every machine (docs/05 §5.2.4)");
            }
        }
    }

    internal static string? Effect(Symbol symbol) {
        for (var current = symbol; current is not null; current = current.ContainingSymbol) {
            var inCore = current.ContainingSymbol is ModuleSymbol { Name: CoreLibrary.ModuleName };
            switch (current) {
                case NamedTypeSymbol { Name: "Runtime" or "Task" or "Region" } type when inCore:
                    return type.Name;
                case FunctionSymbol { Name: "print" } when inCore:
                    return "print";
            }
        }

        return null;
    }
}

/// <summary>
///     Where a package's manifest is (docs/05 §5.2): <c>package.sb</c> and,
///     at a workspace root, <c>workspace.sb</c> — Sable, evaluated — or
///     <c>sable.toml</c>, the canonical form they evaluate to, read as it is.
/// </summary>
public static class ManifestFiles {
    public const string PackageFile = "package.sb";
    public const string WorkspaceFile = "workspace.sb";
    public const string TomlFile = "sable.toml";

    /// <summary>
    ///     Whether the directory has the file under exactly that name: where
    ///     names ignore case, a source file <c>Package.sb</c> is no manifest.
    /// </summary>
    static bool Has(string directory, string name) =>
        File.Exists(Path.Combine(directory, name))
        && Directory.EnumerateFiles(directory, name).Any(f => Path.GetFileName(f) == name);

    /// <summary>Whether the directory has a manifest of either kind.</summary>
    public static bool Exists(string directory) =>
        Has(directory, PackageFile)
        || Has(directory, WorkspaceFile)
        || Has(directory, TomlFile);

    /// <summary>The file a person edits: <c>package.sb</c>, else <c>workspace.sb</c>, else <c>sable.toml</c>.</summary>
    public static string PathIn(string directory) {
        foreach (var name in (string[])[PackageFile, WorkspaceFile]) {
            if (Has(directory, name)) {
                return Path.Combine(directory, name);
            }
        }

        return Path.Combine(directory, TomlFile);
    }

    /// <summary>Every manifest file in the directory, in a fixed order: what a cache of anything built from them hashes.</summary>
    public static string Text(string directory) {
        var text = new StringBuilder();
        foreach (var name in (string[])[PackageFile, WorkspaceFile, TomlFile]) {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) {
                text.Append(name).Append('\n').Append(File.ReadAllText(path)).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    ///     The workspace a member's settings come from: the nearest manifest
    ///     above whose [workspace] lists the directory as a member, or none.
    /// </summary>
    static WorkspaceInheritance? WorkspaceAbove(string directory) {
        for (var parent = Directory.GetParent(directory); parent is not null; parent = parent.Parent) {
            if (!Exists(parent.FullName)) {
                continue;
            }

            if (Load(parent.FullName).Workspace is { } workspace && Workspace.MemberDirectories(parent.FullName, workspace).Contains(directory)) {
                return new(parent.FullName, PathIn(parent.FullName), workspace);
            }
        }

        return null;
    }

    /// <summary>The manifest of the package or workspace in <paramref name="directory" />.</summary>
    /// <exception cref="ManifestException">It is not valid, or the directory has both kinds.</exception>
    public static Manifest Load(string directory) {
        directory = Path.GetFullPath(directory);
        var package = Path.Combine(directory, PackageFile);
        var workspace = Path.Combine(directory, WorkspaceFile);
        var toml = Path.Combine(directory, TomlFile);
        var hasSable = Has(directory, PackageFile) || Has(directory, WorkspaceFile);

        if (hasSable && Has(directory, TomlFile)) {
            throw new ManifestException([
                new(toml, 1, "manifest",
                    $"'{directory}' has both {(Has(directory, PackageFile) ? PackageFile : WorkspaceFile)} and {TomlFile}; a package has one manifest, so delete one"),
            ]);
        }

        if (!hasSable) {
            var written = File.ReadAllText(toml);
            return Manifest.Parse(written, toml, Manifest.UsesInheritance(written) ? WorkspaceAbove(directory) : null);
        }

        // A package and its workspace in one directory: one manifest of both.
        var text = new StringBuilder();
        if (Has(directory, PackageFile)) {
            text.Append(ManifestEvaluator.Evaluate(package));
        }

        if (Has(directory, WorkspaceFile)) {
            text.Append('\n').Append(ManifestEvaluator.Evaluate(workspace));
        }

        var source = PathIn(directory);
        try {
            return Manifest.Parse(text.ToString(), source).Relocated(source);
        } catch (ManifestException e) {
            // A value the reader refuses (a version that is not semver, a
            // feature naming nothing) is the manifest's error, not the TOML's.
            throw new ManifestException([..e.Errors.Select(error => error with { File = source, Line = 1 })]);
        }
    }
}
