using System.Text;
using System.Text.RegularExpressions;
using Sable.CodeGen.Tests;
using Sable.Driver.Packages;
using Sable.SIL.Tests;
using Sable.Syntax;

namespace Sable.Fuzz;

/// <summary>A promise an input broke: what is reported as a failure, as is anything else a target throws.</summary>
public sealed class BrokenPromise(string message) : Exception(message);

/// <summary>
///     Something to fuzz: an action on one input that returns when all is
///     well and throws when it is not, with its limits and what its corpus
///     starts from.
/// </summary>
public abstract class Target {
    public abstract string Name { get; }

    /// <summary>The longest input worth trying: past this, an input is slower and no likelier to find something.</summary>
    public virtual int MaxLength => 32 * 1024;

    /// <summary>How long one input may take: longer is a hang.</summary>
    public virtual TimeSpan Limit => TimeSpan.FromSeconds(20);

    public abstract void Run(ReadOnlySpan<byte> data);

    /// <summary>The corpus's first inputs, by name.</summary>
    public abstract IEnumerable<(string Name, byte[] Data)> Seeds(string root);

    /// <summary>Tokens a mutation inserts whole, for libFuzzer's -dict and the in-process loop alike.</summary>
    public abstract IEnumerable<string> Dictionary();

    public static readonly IReadOnlyList<Target> All = [new ParserTarget(), new FrontEndTarget(), new ResolverTarget(), new PackageFilesTarget()];

    public static Target? Named(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>An input as text: as UTF-8, with what does not decode replaced, so that every input is some text.</summary>
    protected static string Text(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data);

    /// <summary>Every Sable source under the given directories of the repository.</summary>
    protected static IEnumerable<(string, byte[])> SourcesUnder(string root, params string[] directories) {
        foreach (var directory in directories) {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) {
                continue;
            }

            foreach (var file in Directory.GetFiles(path, "*.sb", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                yield return (Path.GetRelativePath(root, file).Replace('/', '_').Replace('\\', '_'), File.ReadAllBytes(file));
            }
        }
    }

    /// <summary>
    ///     Sable's tokens, from the lexer's own tables — every keyword and
    ///     every fixed-text token, so that one added to the language is in the
    ///     dictionary without anyone remembering — and the shapes the tables
    ///     cannot give: literals, comments, interpolation, directives.
    /// </summary>
    protected static IEnumerable<string> SableTokens() =>
        SyntaxFacts.KeywordTexts
            .Concat(Enum.GetValues<SyntaxKind>().Select(SyntaxFacts.GetText).OfType<string>())
            .Concat([
                "1", "0", "42", "0x1F", "0b101", "0o17", "1_000", "1.5", "1e10", "2.0e-3", "'c'", "'\\n'", "'\\u{1F600}'",
                "\"s\"", "\"\"", "\"\\(x)\"", "\"a\\(b)c\\(d)\"", "\"\\n\\t\\\\\"", "\"\"\"\n", "#\"raw\"#",
                "//", "// c\n", "/*", "*/", "/* /* */ */", "///", "/// doc\n",
                "#if", "#elseif", "#else", "#endif", "os(linux)", "arch(arm64)",
                "\n", "\r\n", "\t", "    ", "a", "x", "T", "Self", "self", "Int", "String", "List", "U8[>..]", "I32", "F64", "Bool",
                "module M\n", "import Sable.IO\n", "@main\n", "@inline_layout\n", "@export(\"c\", \"f\")\n",
                "fn f() {}", "struct S {}", "&<u32>", "$0", ".self", "..<", "...",
            ])
            .Where(static text => text.Length > 0)
            .Distinct(StringComparer.Ordinal);
}

/// <summary>
///     The parser: an input parsed as a file, a statement, an expression and
///     a type must give back its text, make a tree whose every node's children
///     cover it exactly, and, edited, reparse incrementally to the very tree a
///     full parse of the edited text gives.
/// </summary>
/// <remarks>
///     The edit comes from the input: what follows its first NUL replaces a
///     span the NUL's tail picks, and an input with no NUL has its middle
///     third deleted.
/// </remarks>
public sealed class ParserTarget : Target {
    public override string Name => "parser";

    public override TimeSpan Limit => TimeSpan.FromSeconds(10);

    public override void Run(ReadOnlySpan<byte> data) {
        var text = Text(data);
        var nul = text.IndexOf('\0', StringComparison.Ordinal);
        var source = nul < 0 ? text : text[..nul];

        Check(SyntaxTree.Parse(source), source, "a file");
        Check(SyntaxTree.ParseStatement(source), source, "a statement");
        Check(SyntaxTree.ParseExpression(source), source, "an expression");
        Check(SyntaxTree.ParseType(source), source, "a type");

        var change = nul < 0 ? new TextChange(new(source.Length / 3, source.Length / 3), "") : Edit(source.Length, text[(nul + 1)..]);
        var before = SyntaxTree.Parse(source);
        var incremental = before.WithChange(change);
        var full = SyntaxTree.Parse(before.Text.WithChange(change), before.Options);
        if (incremental.ToFullString() != full.Text.Text) {
            throw new BrokenPromise($"the incremental tree does not give back the edited text; edit {change}");
        }

        if (Dump(incremental.Root) != Dump(full.Root)) {
            throw new BrokenPromise($"the incremental tree differs from a full parse; edit {change}\n---- full ----\n{Dump(full.Root)}\n---- incremental ----\n{Dump(incremental.Root)}");
        }

        var expected = string.Join(" ", full.Diagnostics.Select(static d => $"{d.Id}@{d.Span}"));
        var actual = string.Join(" ", incremental.Diagnostics.Select(static d => $"{d.Id}@{d.Span}"));
        if (expected != actual) {
            throw new BrokenPromise($"the incremental parse reports {actual}, a full parse {expected}; edit {change}");
        }
    }

    /// <summary>An edit of a source of this length, spelled by the input's tail: where and how much to replace with the tail.</summary>
    static TextChange Edit(int length, string replacement) {
        var hash = 2166136261u;
        foreach (var c in replacement) {
            hash = (hash ^ c) * 16777619u;
        }

        var start = (int)(hash % (uint)(length + 1));
        var removed = (int)(hash / 7 % 33);
        return new(new(start, Math.Min(removed, length - start)), replacement);
    }

    static void Check(SyntaxTree tree, string source, string what) {
        if (tree.ToFullString() != source) {
            throw new BrokenPromise($"parsed as {what}, the tree does not give back its text");
        }

        Tiles(tree.Root, what);
        foreach (var diagnostic in tree.Diagnostics) {
            if (diagnostic.Span.Start < 0 || diagnostic.Span.End > source.Length) {
                throw new BrokenPromise($"parsed as {what}, {diagnostic.Id} is reported at {diagnostic.Span}, outside the text of {source.Length} characters");
            }
        }
    }

    /// <summary>A node's children, nodes and tokens, cover it exactly, in order, and know it as their parent.</summary>
    static void Tiles(SyntaxNode node, string what) {
        var at = node.Position;
        foreach (var child in node.ChildNodesAndTokens()) {
            if (child.Position != at) {
                throw new BrokenPromise($"parsed as {what}, a {child.Kind} in a {node.Kind} starts at {child.Position}, not at {at} where the one before it ends");
            }

            var parent = child.IsNode ? child.AsNode()!.Parent : child.AsToken().Parent;
            if (!ReferenceEquals(parent, node)) {
                throw new BrokenPromise($"parsed as {what}, a {child.Kind} in a {node.Kind} has a {parent?.Kind.ToString() ?? "missing"} parent");
            }

            at += child.FullSpan.Length;
            if (child.AsNode() is { } inner) {
                Tiles(inner, what);
            }
        }

        if (at != node.Position + node.FullWidth) {
            throw new BrokenPromise($"parsed as {what}, a {node.Kind}'s children cover {at - node.Position} of its {node.FullWidth} characters");
        }
    }

    /// <summary>A tree's whole shape: every node's kind and place, and every token's text with its trivia.</summary>
    static string Dump(SyntaxNode root) {
        var builder = new StringBuilder();

        void Write(SyntaxNode node, int depth) {
            builder.Append(' ', depth).Append(node.Kind).Append(' ').Append(node.FullSpan).Append('\n');
            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.AsNode() is { } inner) {
                    Write(inner, depth + 1);
                } else {
                    var token = child.AsToken();
                    builder.Append(' ', depth + 1).Append(token.Kind).Append(' ').Append(token.FullSpan).Append(' ')
                        .Append(System.Text.Json.JsonSerializer.Serialize(token.ToFullString())).Append('\n');
                }
            }
        }

        Write(root, 0);
        return builder.ToString();
    }

    public override IEnumerable<(string, byte[])> Seeds(string root) =>
        SourcesUnder(root, "tests/corpus", "tests/programs", "tests/negative", "examples", "lib");

    public override IEnumerable<string> Dictionary() => SableTokens().Append("\0");
}

/// <summary>The whole front end, held to <see cref="FrontEndCheck" />: what the mutation fuzzer of the SIL tests holds it to.</summary>
public sealed class FrontEndTarget : Target {
    public override string Name => "frontend";

    public override void Run(ReadOnlySpan<byte> data) => FrontEndCheck.Compile(Text(data));

    public override IEnumerable<(string, byte[])> Seeds(string root) =>
        SourcesUnder(root, "tests/corpus", "tests/programs", "tests/negative", "examples", "lib");

    public override IEnumerable<string> Dictionary() => SableTokens();
}

/// <summary>
///     The package resolver, on a registry written as text
///     (<see cref="MemoryRegistry.Parse" />): it ends with an answer that
///     satisfies every requirement and chooses only what is needed, or with a
///     <see cref="ResolutionException" /> that explains itself and is true —
///     where trying every assignment is cheap, none works. And the version
///     parsers under it: every word that reads as a version writes back as
///     the same version, and no text makes them throw.
/// </summary>
public sealed class ResolverTarget : Target {
    public override string Name => "resolver";

    public override int MaxLength => 4096;

    public override TimeSpan Limit => TimeSpan.FromSeconds(10);

    public override void Run(ReadOnlySpan<byte> data) {
        var text = Text(data);
        foreach (var word in text.Split([' ', '\n', '\t', '\'', ':', ','], StringSplitOptions.RemoveEmptyEntries)) {
            if (SemVersion.TryParse(word, out var version, out _)
                && (!SemVersion.TryParse(version.ToString(), out var again, out _) || again != version || again.ToString() != version.ToString())) {
                throw new BrokenPromise($"the version '{word}' writes as '{version}', which does not read back as the same version");
            }
        }

        foreach (var quoted in text.Split('\'').Where(static (_, i) => i % 2 == 1)) {
            VersionReq.TryParse(quoted, out _, out _);
        }

        var registry = MemoryRegistry.Parse(text);
        IReadOnlyDictionary<string, SemVersion> solution;
        try {
            solution = Resolver.Resolve(registry, "root", ResolverCheck.RootVersion, registry.Locked);
        } catch (ResolutionException failure) {
            if (string.IsNullOrWhiteSpace(failure.Message)) {
                throw new BrokenPromise($"the resolver failed with an empty explanation; the registry:\n{registry}");
            }

            if (!registry.HasPrereleases && ResolverCheck.Assignments(registry) <= 100_000 && ResolverCheck.BruteForce(registry) is { } exists) {
                throw new BrokenPromise($"the resolver found no solution, but {ResolverCheck.Describe(exists)} is one\n{failure.Message}\nthe registry:\n{registry}");
            }

            return;
        }

        if (ResolverCheck.Violation(registry, solution) is { } violation) {
            throw new BrokenPromise($"{violation}; chose {ResolverCheck.Describe(solution)}\nthe registry:\n{registry}");
        }
    }

    public override IEnumerable<(string, byte[])> Seeds(string root) {
        string[] registries = [
            "root 1.0.0: a '^1.0'\na 1.0.0: b '>=0.1, <2'\na 1.2.0: b '~1.1'\nb 0.1.0:\nb 1.1.0:\nb 1.1.5:\n",
            "root 1.0.0: a '1', b '2'\na 1.0.0: c '^1'\nb 2.0.0: c '^2'\nc 1.0.0:\nc 2.0.0:\n",
            "root 1.0.0: a '*'\na 1.0.0 (yanked):\na 0.9.0:\nlocked: a 1.0.0\n",
            "root 1.0.0: a '>=1.0.0-alpha'\na 1.0.0-alpha.1:\na 1.0.0-beta.2: root '=1.0.0'\na 1.0.0:\n",
            "root 1.0.0: a '=0.1.1'\na 0.1.1: a '0.1'\na 0.2.0:\nlocked: a 9.9.9\n",
            "root 1.0.0: ghost '1'\n",
            "root 1.0.0: a '^0.0.1', b '~>1'\na 0.0.1: b '<=1.0.1'\nb 1.0.0+build.7:\nb 1.0.1:\nb 3.0.0:\n",
            "root 1.0.0: a '>1, <3', b '1.x'\na 2.0.0: b '>=1.2.3'\nb 1.2.3: a '2.*'\nb 1.0.0:\n",
        ];
        return registries.Select(static (r, i) => ($"registry-{i}", Encoding.UTF8.GetBytes(r)));
    }

    public override IEnumerable<string> Dictionary() => [
        "root", "root 1.0.0: ", "locked: ", " (yanked)", ": ", "'", ", ", "\n", " '^1.0'", " '>=0.1, <2'",
        "^", "~", "=", ">=", ">", "<", "<=", "==", "~>", "*", "x", "X", ".", "-", "+",
        "0", "1", "2", "10", "00", "01", "0.0.1", "0.1.0", "1.0.0", "1.2.3", "2.0.0", "-alpha", "-alpha.1", "-beta.2", "-rc.1", "+build.7",
        "9223372036854775807", "99999999999999999999", "a", "b", "c", "ghost",
    ];
}

/// <summary>
///     The files a package is described by: a manifest (<c>sable.toml</c>), a
///     lock file and a registry's index of a package. Each refuses what it
///     cannot read with its own exception, never another; and a lock file
///     and an index that read write back as text that reads as them again.
/// </summary>
public sealed partial class PackageFilesTarget : Target {
    public override string Name => "packages";

    public override int MaxLength => 8192;

    public override TimeSpan Limit => TimeSpan.FromSeconds(10);

    public override void Run(ReadOnlySpan<byte> data) {
        var text = Text(data);
        try {
            TomlReader.Parse(text);
        } catch (TomlException) {
        }

        try {
            Manifest.Parse(text);
        } catch (ManifestException) {
        }

        try {
            var written = LockFile.Parse(text).Write();
            if (LockFile.Parse(written).Write() != written) {
                throw new BrokenPromise($"a lock file does not write back as it reads:\n{written}");
            }
        } catch (PackageException) {
        }

        try {
            var written = PackageIndex.Parse("p", text).Write();
            if (PackageIndex.Parse("p", written).Write() != written) {
                throw new BrokenPromise($"a registry's index does not write back as it reads:\n{written}");
            }
        } catch (RegistryException) {
        }
    }

    public override IEnumerable<(string, byte[])> Seeds(string root) {
        // Every manifest, lock file and index the package tests write out.
        var seeds = new List<(string, byte[])>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "tests", "Sable.CodeGen.Tests"), "*.cs").Order(StringComparer.Ordinal)) {
            var blocks = RawStrings().Matches(File.ReadAllText(file)).Select(static m => m.Groups[1].Value)
                .Where(static b => b.Contains("[package]", StringComparison.Ordinal) || b.Contains("[workspace]", StringComparison.Ordinal)
                    || b.Contains("[[package]]", StringComparison.Ordinal) || b.Contains("\"versions\"", StringComparison.Ordinal));
            seeds.AddRange(blocks.Select((b, i) => ($"{Path.GetFileNameWithoutExtension(file)}-{i}", Encoding.UTF8.GetBytes(b))));
        }

        const string lockFile = """
                                # sable.lock: written by sable, to be read by sable. Do not edit it by hand.
                                version = 1

                                [[package]]
                                name = "json"
                                version = "1.2.3"
                                source = "registry"
                                checksum = "0123456789abcdef"
                                dependencies = [
                                    "text 0.4.0",
                                ]

                                [[package]]
                                name = "text"
                                version = "0.4.0-rc.1+b"
                                source = "git+https://example.com/t.git?tag=v0.4#abc123"
                                """;
        const string index = """
                             {
                               "name": "p",
                               "owner": "k1",
                               "owners": ["k1", "k2"],
                               "keys": ["k1", "k2", "k0"],
                               "versions": [
                                 {
                                   "version": "1.0.0",
                                   "checksum": "aa",
                                   "dependencies": [
                                     { "name": "q", "req": "^1", "kind": "normal", "optional": false, "default-features": true, "features": ["x"] }
                                   ],
                                   "features": { "default": ["x"], "x": [] },
                                   "yanked": false,
                                   "key": "k1",
                                   "signature": "sig",
                                   "description": "the package"
                                 },
                                 {
                                   "version": "1.1.0-beta",
                                   "checksum": "bb",
                                   "dependencies": [],
                                   "features": {},
                                   "yanked": true,
                                   "key": "k2",
                                   "signature": "sig2"
                                 }
                               ]
                             }
                             """;
        seeds.Add(("lock", Encoding.UTF8.GetBytes(lockFile)));
        seeds.Add(("index", Encoding.UTF8.GetBytes(index)));
        return seeds;
    }

    [GeneratedRegex("\"\"\"\\n(.*?)\"\"\"", RegexOptions.Singleline)]
    private static partial Regex RawStrings();

    public override IEnumerable<string> Dictionary() => [
        "[", "]", "[[", "]]", "{", "}", "=", "\"", "'", "\"\"\"", "'''", ",", ".", "#", "\n", "\\", "\\u0041", "\\n",
        "true", "false", "0x1F", "0o7", "0b1", "1_000", "1.5", "1e3", "inf", "nan", "-", "+", "1979-05-27T07:32:00Z",
        "[package]", "[dependencies]", "[dev-dependencies]", "[build-dependencies]", "[features]", "[workspace]", "[[bin]]", "[[package]]",
        "[target.'cfg(os = \"linux\")'.dependencies]", "[lints]", "[profile.release]",
        "name", "version", "edition", "description", "path", "git", "tag", "branch", "rev", "features", "default-features", "optional",
        "members", "default", "source", "checksum", "dependencies", "registry", "license", "authors",
        "\"name\"", "\"version\"", "\"owner\"", "\"owners\"", "\"keys\"", "\"versions\"", "\"checksum\"", "\"dependencies\"", "\"req\"",
        "\"kind\"", "\"optional\"", "\"default-features\"", "\"features\"", "\"yanked\"", "\"key\"", "\"signature\"", "\"description\"",
        "null", ":", "1.0.0", "\"1.0.0\"", "\"^1\"", "\"registry\"",
    ];
}
