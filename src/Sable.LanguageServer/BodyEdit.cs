using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     How the workspace now differs from an older analysis's snapshot, when
///     every difference is inside members' bodies: every declaration is as
///     that analysis has it, so its symbols are still the right ones. Each
///     edited member is bound again, speculatively, against the old
///     compilation; the rest of each edited file is the old one, moved by
///     what the edits before it added.
/// </summary>
/// <remarks>
///     A body cannot change what anything outside it means: a function with a
///     block body declares its result (no result is <c>Void</c>), and an
///     accessor's property or subscript declares its type. A field's
///     initializer, an expression body and a closure can make a type, so an
///     edit in one of those is not taken.
/// </remarks>
internal sealed class BodyEdits {
    readonly Dictionary<Document, BodyEdit> _byOld = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<SyntaxTree, BodyEdit> _byTree = new(ReferenceEqualityComparer.Instance);

    BodyEdits(IEnumerable<BodyEdit> edits) {
        foreach (var edit in edits) {
            _byOld[edit.Old] = edit;
            _byTree[edit.Document.Tree] = edit;
        }
    }

    /// <summary>The edit of a document as the analysis has it, if it changed.</summary>
    public BodyEdit? OfOld(Document old) => _byOld.GetValueOrDefault(old);

    /// <summary>The edit that made a document as it is now, if it changed.</summary>
    public BodyEdit? OfCurrent(Document document) => _byTree.GetValueOrDefault(document.Tree);

    /// <summary>The edit whose new text a node is in, if any.</summary>
    public BodyEdit? Containing(SyntaxNode node) {
        var root = node;
        while (root.Parent is { } parent) {
            root = parent;
        }

        return _byTree.Values.FirstOrDefault(e => ReferenceEquals(e.Document.Tree.Root, root));
    }

    /// <summary>
    ///     Whether an analysis of an older snapshot answers for this one
    ///     about declarations and references: the same packages and files,
    ///     and each file that changed, changed inside members' bodies only.
    ///     <paramref name="edits" /> are those changes, or null when nothing
    ///     changed.
    /// </summary>
    public static bool Between(Analysis analysis, WorkspaceSnapshot snapshot, out BodyEdits? edits) {
        edits = null;
        var older = analysis.Snapshot;
        if (analysis.HasGeneratedTrees || !ReferenceEquals(older.Layout, snapshot.Layout) || older.Documents.Count != snapshot.Documents.Count) {
            return false;
        }

        var changed = new List<BodyEdit>();
        var outside = false;
        foreach (var (key, document) in snapshot.Documents) {
            if (older.Documents.GetValueOrDefault(key) is not { } old) {
                return false;
            }

            if (ReferenceEquals(old.Tree, document.Tree)) {
                continue;
            }

            if (!analysis.Contains(old.Tree)) {
                // A file the package does not compile itself, which may still
                // be one its dependencies' trees were read from.
                outside = true;
                continue;
            }

            if (BodyEdit.Between(old, document) is not { } edit) {
                return false;
            }

            changed.Add(edit);
        }

        if (outside && !analysis.IsCurrentIn(WithOld(snapshot, older, changed))) {
            return false;
        }

        edits = changed.Count == 0 ? null : new(changed);
        return true;
    }

    /// <summary>The snapshot with the edited documents as they were, to ask whether the rest is current.</summary>
    static WorkspaceSnapshot WithOld(WorkspaceSnapshot snapshot, WorkspaceSnapshot older, List<BodyEdit> changed) {
        var documents = snapshot.Documents;
        foreach (var edit in changed) {
            documents = documents.SetItem(Workspace.KeyOf(edit.Old.Uri), edit.Old);
        }

        return new(documents, snapshot.Generation, snapshot.Layout);
    }
}

/// <summary>One document's edits inside members' bodies: see <see cref="BodyEdits" />.</summary>
internal sealed class BodyEdit {
    readonly List<(SyntaxNode Old, SyntaxNode New)> _members;
    readonly Dictionary<SyntaxNode, SemanticModel> _speculative = new(ReferenceEqualityComparer.Instance);

    BodyEdit(Document old, Document document, List<(SyntaxNode Old, SyntaxNode New)> members) {
        Old = old;
        Document = document;
        _members = members;
    }

    /// <summary>The document as the analysis has it.</summary>
    public Document Old { get; }

    /// <summary>The document as it is.</summary>
    public Document Document { get; }

    /// <summary>
    ///     The edit from one text of a document to another, if the two are
    ///     the same but inside the bodies of members, which keep their order
    ///     and declarations; null if they differ anywhere else.
    /// </summary>
    public static BodyEdit? Between(Document old, Document document) {
        var oldMembers = Analysis.MembersOf(old.Tree.Root).ToList();
        var newMembers = Analysis.MembersOf(document.Tree.Root).ToList();
        if (oldMembers.Count != newMembers.Count || Skeleton(old.Tree, oldMembers) != Skeleton(document.Tree, newMembers)) {
            return null;
        }

        var members = new List<(SyntaxNode, SyntaxNode)>();
        for (var i = 0; i < oldMembers.Count; i++) {
            var (a, b) = (oldMembers[i], newMembers[i]);
            if (a.Kind != b.Kind) {
                return null;
            }

            if (!old.Tree.Text.Text.AsSpan(a.FullSpan.Start, a.FullSpan.Length).SequenceEqual(document.Tree.Text.Text.AsSpan(b.FullSpan.Start, b.FullSpan.Length))) {
                members.Add((a, b));
            }
        }

        // None changed, when the file was parsed again with the same text —
        // opened after it was read from disk: an edit that moves nothing.
        return new(old, document, members);
    }

    /// <summary>
    ///     A file's text with what is inside each member's body left out:
    ///     two texts with the same skeleton differ inside bodies alone.
    /// </summary>
    static string Skeleton(SyntaxTree tree, List<SyntaxNode> members) {
        var text = tree.Text.Text;
        var skeleton = new StringBuilder(text.Length);
        var at = 0;
        foreach (var member in members) {
            foreach (var body in Bodies(member)) {
                skeleton.Append(text, at, body.OpenBraceToken.Span.End - at).Append('\0');
                at = body.CloseBraceToken.Span.Start;
            }
        }

        return skeleton.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>The blocks of a member whose content cannot change a declaration's type, outermost only, in order.</summary>
    static IEnumerable<BlockSyntax> Bodies(SyntaxNode member) {
        foreach (var child in member.ChildNodes()) {
            if (child is BlockSyntax block) {
                if (IsBody(block) && !block.OpenBraceToken.IsMissing && !block.CloseBraceToken.IsMissing) {
                    yield return block;
                }

                continue;
            }

            foreach (var nested in Bodies(child)) {
                yield return nested;
            }
        }
    }

    /// <summary>A block whose content cannot change a declaration's type: see <see cref="BodyEdits" />.</summary>
    static bool IsBody(BlockSyntax block) =>
        block.Parent switch {
            FunctionDeclarationSyntax function => ReferenceEquals(function.Body, block),
            InitDeclarationSyntax init => ReferenceEquals(init.Body, block),
            DeinitDeclarationSyntax deinit => ReferenceEquals(deinit.Body, block),
            OperatorDeclarationSyntax op => ReferenceEquals(op.Body, block),
            AccessorSyntax accessor => ReferenceEquals(accessor.Body, block)
                && accessor.Parent?.Parent is SubscriptDeclarationSyntax or FieldDeclarationSyntax { Declarator.TypeAnnotation: not null },
            _ => false,
        };

    /// <summary>The edited member a position of the document as it is falls in, if any.</summary>
    public SyntaxNode? NewMemberAt(int offset) {
        foreach (var (_, member) in _members) {
            if (member.Span.Start <= offset && offset <= member.Span.End) {
                return member;
            }
        }

        return null;
    }

    /// <summary>Whether a span of the old document falls in an edited member, which the new text replaces.</summary>
    public bool InOldMember(TextSpan span) => _members.Any(m => span.End > m.Old.Span.Start && span.Start < m.Old.Span.End);

    /// <summary>The edited members as they are now.</summary>
    public IEnumerable<SyntaxNode> NewMembers => _members.Select(static m => m.New);

    /// <summary>Where an offset of the document as it is, outside the edited members, was in the old one.</summary>
    public int ToOld(int offset) {
        var shift = 0;
        foreach (var (old, member) in _members) {
            if (member.FullSpan.End <= offset) {
                shift += member.FullSpan.Length - old.FullSpan.Length;
            }
        }

        return offset - shift;
    }

    /// <summary>Where a span of the old document, outside the edited members, is now.</summary>
    public TextSpan ToNew(TextSpan span) {
        var shift = 0;
        foreach (var (old, member) in _members) {
            if (old.FullSpan.End <= span.Start) {
                shift += member.FullSpan.Length - old.FullSpan.Length;
            }
        }

        return new(span.Start + shift, span.Length);
    }

    /// <summary>An edited member bound as it is, against the analysis. Hold its gate.</summary>
    public SemanticModel Speculative(Analysis analysis, SyntaxNode member) {
        if (!_speculative.TryGetValue(member, out var model)) {
            var old = _members.First(m => ReferenceEquals(m.New, member)).Old;
            _speculative[member] = model = analysis.Model(Old.Tree).GetSpeculativeModel(old, member);
        }

        return model;
    }
}
