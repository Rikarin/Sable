using System.Text.RegularExpressions;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     One compilation over one package of one workspace snapshot: its
///     documents, its dependencies' files, and the library modules they
///     import — as <c>sable check</c> compiles it. Built once per snapshot
///     and package that something asks about, and bound lazily, one member
///     at a time. Files in no package are compiled together, as one loose
///     program.
/// </summary>
/// <remarks>
///     A compilation is not thread-safe — it binds lazily and caches as it
///     goes — so everything that touches it holds <see cref="Gate" />, for as
///     short a time as it can: a member's binding, not a file's.
/// </remarks>
internal sealed partial class Analysis {
    readonly Dictionary<SyntaxNode, Document> _documentsByRoot = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<SyntaxNode, SyntaxTree> _treesByRoot = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<SyntaxTree> _library = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<SyntaxTree> _dependencies = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<SyntaxTree, IReadOnlyList<Diagnostic>> _diagnostics = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<SyntaxTree, IReadOnlyList<Diagnostic>> _lints = new(ReferenceEqualityComparer.Instance);
    IReadOnlyList<Diagnostic>? _unplaced;

    public object Gate { get; } = new();
    public WorkspaceSnapshot Snapshot { get; }
    public Compilation Compilation { get; }

    /// <summary>The package it compiles (<see cref="ProjectLayout.KeyOf" />).</summary>
    public string Key { get; }

    /// <summary>The level of each lint in this package: its manifest's <c>[lints]</c>.</summary>
    public LintConfiguration Lints { get; }

    /// <summary>The snapshot's documents in this compilation: the package's own, and those of its dependencies the editor has.</summary>
    public IReadOnlyList<Document> Documents { get; }

    Analysis(
        WorkspaceSnapshot snapshot, string key, LintConfiguration lints, Compilation compilation, IEnumerable<SyntaxTree> library, IEnumerable<SyntaxTree> dependencies) {
        Snapshot = snapshot;
        Key = key;
        Lints = lints;
        Compilation = compilation;
        foreach (var tree in compilation.SyntaxTrees) {
            _treesByRoot[tree.Root] = tree;
        }

        foreach (var document in snapshot.Documents.Values) {
            if (_treesByRoot.TryGetValue(document.Tree.Root, out var tree) && ReferenceEquals(tree, document.Tree)) {
                _documentsByRoot[document.Tree.Root] = document;
            }
        }

        Documents = [.._documentsByRoot.Values.OrderBy(static d => d.Uri, StringComparer.Ordinal)];
        foreach (var tree in dependencies.Where(t => !_documentsByRoot.ContainsKey(t.Root))) {
            _dependencies.Add(tree);
        }

        foreach (var tree in library) {
            _library.Add(tree);
        }

        foreach (var tree in CoreLibrary.Trees) {
            _library.Add(tree);
        }
    }

    /// <summary>
    ///     Builds the compilation of one package — its documents, then its
    ///     dependencies' trees — and binds every signature, so that what
    ///     comes after — a member's body — is all that is left to bind.
    /// </summary>
    public static Analysis Build(
        WorkspaceSnapshot snapshot,
        string key,
        IReadOnlyList<SyntaxTree> dependencies,
        Func<SyntaxTree, IReadOnlyList<SyntaxTree>> libraryFor,
        LintConfiguration? lints = null) {
        var trees = snapshot.Documents.Values
            .Where(d => snapshot.KeyOf(d) == key)
            .Select(static d => d.Tree)
            .OrderBy(static t => t.Text.FileName, StringComparer.Ordinal)
            .ToList();
        var own = trees.ToHashSet(ReferenceEqualityComparer.Instance);
        trees.AddRange(dependencies.Where(t => own.Add(t)));

        // A library module the workspace has its own files for — the
        // compiler's repository, its lib/ read from disk — is those files:
        // the embedded copy would declare everything in it a second time.
        var declared = trees.Select(CoreLibrary.ModuleOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var library = new List<SyntaxTree>();
        var seen = new HashSet<SyntaxTree>(ReferenceEqualityComparer.Instance);
        foreach (var tree in trees) {
            foreach (var needed in libraryFor(tree)) {
                if (CoreLibrary.ModuleOf(needed) is { } module && declared.Contains(module)) {
                    continue;
                }

                if (seen.Add(needed)) {
                    library.Add(needed);
                }
            }
        }

        var compilation = Compilation.Create([..trees, ..library]);
        var analysis = new Analysis(snapshot, key, lints ?? LintConfiguration.Default, compilation, library, dependencies);
        lock (analysis.Gate) {
            // Binding every signature reports the declarations' problems too.
            analysis._unplaced = [..compilation.GetDiagnostics().Except(trees.SelectMany(static t => t.Diagnostics))];
        }

        return analysis;
    }

    /// <summary>The workspace document a node belongs to, or null for the library's.</summary>
    public Document? DocumentOf(SyntaxNode node) => _documentsByRoot.GetValueOrDefault(RootOf(node));

    /// <summary>The tree a node belongs to, the library's included.</summary>
    public SyntaxTree? TreeOf(SyntaxNode node) => _treesByRoot.GetValueOrDefault(RootOf(node));

    public bool IsLibrary(SyntaxTree tree) => _library.Contains(tree);

    /// <summary>Whether the tree is a dependency's file read from disk, not a document of the snapshot.</summary>
    public bool IsDependencyFile(SyntaxTree tree) => _dependencies.Contains(tree);

    public bool Contains(SyntaxTree tree) => _treesByRoot.TryGetValue(tree.Root, out var known) && ReferenceEquals(known, tree);

    /// <summary>
    ///     Whether this analysis is the one a later snapshot would build: the
    ///     same packages, and the same text for every file it compiles — an
    ///     edit in another package does not make this one's compilation, its
    ///     bound members or its lowering out of date.
    /// </summary>
    /// <remarks>
    ///     A dependency's file read from disk is compared by text, as the
    ///     snapshot would give it; one outside the workspace that changes on
    ///     disk is not noticed — no one is told it changed.
    /// </remarks>
    public bool IsCurrentIn(WorkspaceSnapshot snapshot) {
        if (!ReferenceEquals(snapshot.Layout, Snapshot.Layout)) {
            return false;
        }

        foreach (var document in Documents) {
            if (!ReferenceEquals(snapshot.Find(document.Uri)?.Tree, document.Tree)) {
                return false;
            }
        }

        foreach (var tree in _dependencies) {
            if (snapshot.Documents.GetValueOrDefault(Path.GetFullPath(tree.Text.FileName)) is { } document && document.Tree.Text.Text != tree.Text.Text) {
                return false;
            }
        }

        // Nothing added to the package: as many files as it compiles.
        var own = 0;
        foreach (var document in snapshot.Documents.Values) {
            if (snapshot.KeyOf(document) == Key && ++own > Documents.Count) {
                return false;
            }
        }

        return own == Documents.Count(d => Snapshot.KeyOf(d) == Key);
    }

    static SyntaxNode RootOf(SyntaxNode node) {
        while (node.Parent is { } parent) {
            node = parent;
        }

        return node;
    }

    /// <summary>The semantic model of a tree of this compilation. Hold <see cref="Gate" /> to use it.</summary>
    public SemanticModel Model(SyntaxTree tree) => Compilation.GetSemanticModel(tree);

    /// <summary>
    ///     Every member declaration of a file a member model can bind, in
    ///     document order: what binding the whole file binds.
    /// </summary>
    public static IEnumerable<SyntaxNode> MembersOf(SyntaxNode container) {
        IEnumerable<SyntaxNode> members = container switch {
            CompilationUnitSyntax unit => unit.Members,
            StructDeclarationSyntax type => type.Members,
            ClassDeclarationSyntax type => type.Members,
            ActorDeclarationSyntax type => type.Members,
            ProtocolDeclarationSyntax type => type.Members,
            UnionDeclarationSyntax type => type.Members,
            EnumDeclarationSyntax type => type.Members,
            ExtensionDeclarationSyntax type => type.Members,
            _ => [],
        };

        foreach (var member in members) {
            if (SemanticModel.IsBindableMember(member)) {
                yield return member;
            } else {
                foreach (var nested in MembersOf(member)) {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    ///     A document's diagnostics: the parser's, each member's binding, and
    ///     those of declarations and signatures that fall in this file. Taken
    ///     and released the gate member by member, so a completion waits for
    ///     one member at most.
    /// </summary>
    public IReadOnlyList<Diagnostic> DiagnosticsOf(SyntaxTree tree, CancellationToken cancellation) {
        SemanticModel model;
        lock (Gate) {
            if (_diagnostics.TryGetValue(tree, out var known)) {
                return known;
            }

            model = Model(tree);
        }

        var all = new List<Diagnostic>(tree.Diagnostics);
        foreach (var member in MembersOf(tree.Root)) {
            cancellation.ThrowIfCancellationRequested();
            lock (Gate) {
                all.AddRange(model.GetMemberModel(member).GetDiagnostics());
            }
        }

        lock (Gate) {
            // A signature bound lazily while binding a body reports into the
            // compilation's bag too: read it again.
            var unplaced = _unplaced ?? [];
            var now = Compilation.GetDiagnostics().Except(Compilation.SyntaxTrees.SelectMany(static t => t.Diagnostics)).ToList();
            if (now.Count != unplaced.Count) {
                _unplaced = unplaced = now;
            }

            // Where the compilation knows the file, that is the answer; only
            // the rest is placed by looking.
            all.AddRange(unplaced.Where(d => Compilation.FileOf(d) is { } file ? ReferenceEquals(file, tree) : BelongsTo(d, tree)));
            IReadOnlyList<Diagnostic> result = [..all.Distinct().OrderBy(static d => d.Span.Start)];
            _diagnostics[tree] = result;
            return result;
        }
    }

    /// <summary>
    ///     What the lints (docs/05 §5.7) report in a file, at the levels the
    ///     package's <c>[lints]</c> and the file's <c>@allow</c> / <c>@warn</c>
    ///     give them. <c>sable check</c> lints only a program that
    ///     type-checks; here a file is linted once it has no errors of its
    ///     own, so its warnings do not wait for every other file to be right
    ///     — while a lint over code that does not bind would only add noise.
    /// </summary>
    /// <remarks>
    ///     A lint walks the whole file's bound bodies, so this holds
    ///     <see cref="Gate" /> for the whole file: it runs after the type
    ///     errors are out, beside the SIL checks.
    /// </remarks>
    public IReadOnlyList<Diagnostic> LintsOf(SyntaxTree tree, CancellationToken cancellation) {
        if (DiagnosticsOf(tree, cancellation).Any(static d => d.Severity == DiagnosticSeverity.Error)) {
            return [];
        }

        lock (Gate) {
            if (_lints.TryGetValue(tree, out var known)) {
                return known;
            }

            cancellation.ThrowIfCancellationRequested();
            IReadOnlyList<Diagnostic> found = [..Symbols.Lints.Run(Compilation, Lints, [tree])];
            _lints[tree] = found;
            return found;
        }
    }

    /// <summary>
    ///     Whether a diagnostic that came without a file is this file's: a
    ///     guess, for those the compilation could not place itself.
    /// </summary>
    /// <remarks>
    ///     Declaration and signature diagnostics carry a span but not the file
    ///     it is in (<c>Compilation.GetAllDiagnosticsByFile</c> says as much).
    ///     A span that ends past the text cannot be this file's; one that
    ///     starts mid-token cannot either; and a message that quotes a name
    ///     must find it under the span.
    /// </remarks>
    static bool BelongsTo(Diagnostic diagnostic, SyntaxTree tree) {
        var span = diagnostic.Span;
        if (span.End > tree.Text.Length) {
            return false;
        }

        var token = SemanticModel.FindToken(tree.Root, span.Start);
        if (token.IsNone || token.Span.Start != span.Start) {
            return false;
        }

        var quoted = QuotedName().Match(diagnostic.Message);
        if (quoted.Success && span.Length > 0) {
            var text = tree.Text.ToString(span);
            var name = quoted.Groups[1].Value;
            return text.Contains(name, StringComparison.Ordinal) || name.Contains(text, StringComparison.Ordinal);
        }

        return true;
    }

    [GeneratedRegex("'([A-Za-z_][A-Za-z0-9_]*)'")]
    private static partial Regex QuotedName();
}

/// <summary>
///     The analyses the server has built, for each package: the latest, and
///     the one being built for the newest snapshot something asked about.
/// </summary>
internal sealed class AnalysisCache(Func<WorkspaceSnapshot, string, Analysis> build) {
    readonly object _lock = new();
    readonly Dictionary<string, Analysis> _latest = new(StringComparer.Ordinal);
    readonly Dictionary<string, (long Generation, Task<Analysis> Task)> _building = new(StringComparer.Ordinal);

    /// <summary>The newest analysis of a package built, of whatever snapshot: what a completion binds its edit against.</summary>
    public Analysis? Latest(string key) {
        lock (_lock) {
            return _latest.GetValueOrDefault(key);
        }
    }

    /// <summary>The analysis of a package in exactly this snapshot, building it if no one has.</summary>
    public Task<Analysis> GetAsync(WorkspaceSnapshot snapshot, string key) {
        lock (_lock) {
            if (_latest.TryGetValue(key, out var latest) && (latest.Snapshot.Generation == snapshot.Generation || latest.IsCurrentIn(snapshot))) {
                return Task.FromResult(latest);
            }

            if (_building.TryGetValue(key, out var building) && building.Generation == snapshot.Generation) {
                return building.Task;
            }

            var task = Task.Run(() => {
                var analysis = build(snapshot, key);
                lock (_lock) {
                    if (!_latest.TryGetValue(key, out var known) || known.Snapshot.Generation < snapshot.Generation) {
                        _latest[key] = analysis;
                    }
                }

                return analysis;
            });
            _building[key] = (snapshot.Generation, task);
            return task;
        }
    }
}
