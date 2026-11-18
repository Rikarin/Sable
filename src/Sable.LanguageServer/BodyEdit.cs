using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     How a document now differs from its text in an older analysis, when
///     the difference is all inside one member's body: every declaration of
///     the workspace is as that analysis has it, so its symbols are still
///     the right ones. The member is bound again, speculatively, against the
///     old compilation; the rest of the file is the old one, shifted by what
///     the edit added.
/// </summary>
/// <remarks>
///     A body cannot change what anything outside it means: a function with a
///     block body declares its result (no result is <c>Void</c>), and an
///     accessor's property or subscript declares its type. A field's
///     initializer, an expression body and a closure can make a type, so an
///     edit in one of those is not taken.
/// </remarks>
internal sealed class BodyEdit {
    BodyEdit(Document document, Document old, SyntaxNode oldMember, SyntaxNode newMember) {
        Document = document;
        Old = old;
        OldMember = oldMember;
        NewMember = newMember;
        Delta = document.Tree.Text.Length - old.Tree.Text.Length;
    }

    /// <summary>The document as it is.</summary>
    public Document Document { get; }

    /// <summary>The document as the analysis has it.</summary>
    public Document Old { get; }

    public SyntaxNode OldMember { get; }
    public SyntaxNode NewMember { get; }

    /// <summary>How many characters longer the document is now.</summary>
    public int Delta { get; }

    /// <summary>
    ///     Whether an analysis of an older snapshot answers for this one
    ///     about declarations and references: the same packages, the same
    ///     files, each with the same tree but at most one, and that one
    ///     changed inside one member's body only. <paramref name="edit" />
    ///     is that change, or null when nothing changed.
    /// </summary>
    public static bool Between(Analysis analysis, WorkspaceSnapshot snapshot, out BodyEdit? edit) {
        edit = null;
        var older = analysis.Snapshot;
        if (analysis.HasGeneratedTrees || !ReferenceEquals(older.Layout, snapshot.Layout) || older.Documents.Count != snapshot.Documents.Count) {
            return false;
        }

        (Document Old, Document New)? changed = null;
        foreach (var (key, document) in snapshot.Documents) {
            if (older.Documents.GetValueOrDefault(key) is not { } old) {
                return false;
            }

            if (ReferenceEquals(old.Tree, document.Tree)) {
                continue;
            }

            if (changed is not null) {
                return false;
            }

            changed = (old, document);
        }

        if (changed is not var (before, after)) {
            return true;
        }

        if (!analysis.Contains(before.Tree)) {
            // A file the package does not compile itself, which may still be
            // one its dependencies' trees were read from.
            return analysis.IsCurrentIn(snapshot);
        }

        // The span that differs: what is between the longest common start and end.
        var (a, b) = (before.Tree.Text.Text, after.Tree.Text.Text);
        var prefix = 0;
        var shortest = Math.Min(a.Length, b.Length);
        while (prefix < shortest && a[prefix] == b[prefix]) {
            prefix++;
        }

        var suffix = 0;
        while (suffix < shortest - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) {
            suffix++;
        }

        if (BodyAround(before.Tree.Root, prefix, a.Length - suffix) is not { } oldMember
            || BodyAround(after.Tree.Root, prefix, b.Length - suffix) is not { } newMember
            || !ReferenceEquals(LanguageServer.MapMember(before.Tree.Root, newMember), oldMember)
            || oldMember.Span.Start != newMember.Span.Start
            || oldMember.Span.End + (b.Length - a.Length) != newMember.Span.End) {
            return false;
        }

        edit = new(after, before, oldMember, newMember);
        return true;
    }

    /// <summary>The member whose body holds a span, strictly inside its braces; null if none does.</summary>
    static SyntaxNode? BodyAround(SyntaxNode root, int start, int end) {
        var member = SemanticModel.FindMember(root, start);
        if (member is null || !ReferenceEquals(SemanticModel.FindMember(root, end), member)) {
            return null;
        }

        foreach (var block in member.DescendantNodes().OfType<BlockSyntax>()) {
            if (IsBody(block) && block.OpenBraceToken.Span.End <= start && end <= block.CloseBraceToken.Span.Start && !block.CloseBraceToken.IsMissing) {
                return member;
            }
        }

        return null;
    }

    /// <summary>A block whose content cannot change a declaration's type: see the remarks.</summary>
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

    /// <summary>Whether an offset of the document as it is falls in the edited member.</summary>
    public bool InNewMember(int offset) => NewMember.Span.Start <= offset && offset <= NewMember.Span.End;

    /// <summary>Whether a span of the old document falls in the edited member, which the new one replaces.</summary>
    public bool InOldMember(TextSpan span) => span.End > OldMember.Span.Start && span.Start < OldMember.Span.End;

    /// <summary>Where an offset of the document as it is, outside the member, was in the old one.</summary>
    public int ToOld(int offset) => offset >= NewMember.Span.End ? offset - Delta : offset;

    /// <summary>Where a span of the old document, outside the member, is now.</summary>
    public TextSpan ToNew(TextSpan span) => span.Start >= OldMember.Span.End ? new(span.Start + Delta, span.Length) : span;

    /// <summary>The edited member bound as it is, against the analysis. Hold its gate.</summary>
    public SemanticModel Speculative(Analysis analysis) =>
        _speculative ??= analysis.Model(Old.Tree).GetSpeculativeModel(OldMember, NewMember);

    SemanticModel? _speculative;
}
