using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sable.Driver.Commands;
using Sable.Driver.Docs;
using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable doc</c> over the whole standard library: nothing crashes,
///     every public declaration is on a page, and every link lands.
/// </summary>
public sealed partial class DocTests {
    static readonly Lazy<(Compilation Compilation, DocSet Set, IReadOnlyDictionary<string, string> Pages)> Library = new(static () => {
        var compilation = DocCommand.StandardLibraryCompilation();
        var set = DocSet.Build(compilation, DocCommand.StandardLibraryModules);
        return (compilation, set, HtmlDocWriter.Render(set));
    });

    [Fact]
    public void EveryPublicDeclarationOfTheLibraryIsDocumented() {
        var (compilation, set, pages) = Library.Value;
        var documented = set.Entries.Values.Select(static e => e.Symbol).ToHashSet(ReferenceEqualityComparer.Instance);

        var missing = new List<string>();
        foreach (var module in compilation.Modules.Where(static m => m.Name != "Documentation")) {
            foreach (var member in module.Members.Where(static m => m.DeclaredAccessibility == Accessibility.Public)) {
                if (member is NamedTypeSymbol type) {
                    if (type.DeclaringSyntax.IsEmpty && !type.Members.Any(static m => m.DeclaredAccessibility == Accessibility.Public && !m.IsSynthesized)) {
                        continue;
                    }

                    Check(type);
                    var isProtocol = type.TypeKind == TypeKind.Protocol;
                    foreach (var typeMember in type.Members) {
                        if (typeMember.IsSynthesized || (!isProtocol && typeMember.DeclaredAccessibility != Accessibility.Public)
                            || typeMember is FunctionSymbol { FunctionKind: FunctionKind.Accessor or FunctionKind.Deinitializer }) {
                            continue;
                        }

                        Check(typeMember);
                    }
                } else if (member is FunctionSymbol or FieldSymbol or AliasSymbol) {
                    Check(member);
                }
            }
        }

        Assert.True(missing.Count == 0, $"not documented: {string.Join(", ", missing)}");

        // And each entry is on the page its link names.
        foreach (var entry in set.Entries.Values) {
            var href = HtmlDocWriter.Href(entry);
            var (page, anchor) = Split(href);
            Assert.True(pages.ContainsKey(page), $"{entry.Id}: no page {page}");
            if (anchor is not null) {
                Assert.Contains($"id=\"{anchor}\"", pages[page], StringComparison.Ordinal);
            }
        }

        void Check(Symbol symbol) {
            if (!documented.Contains(symbol)) {
                missing.Add($"{symbol.QualifiedName} ({symbol.Kind})");
            }
        }
    }

    [GeneratedRegex("href=\"([^\"]*)\"")]
    private static partial Regex Href();

    [GeneratedRegex("id=\"([^\"]*)\"")]
    private static partial Regex Id();

    static (string Page, string? Anchor) Split(string href) {
        var hash = href.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (href, null) : (href[..hash], href[(hash + 1)..]);
    }

    [Fact]
    public void EveryLinkResolves() {
        var (_, _, pages) = Library.Value;
        var ids = pages.ToDictionary(static p => p.Key, static p => Id().Matches(p.Value).Select(static m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal));
        var broken = new List<string>();
        foreach (var (name, html) in pages) {
            foreach (Match match in Href().Matches(html)) {
                var (page, anchor) = Split(WebUtility.HtmlDecode(match.Groups[1].Value));
                page = page.Length == 0 ? name : page;
                if (!ids.TryGetValue(page, out var targets) || (anchor is not null && !targets.Contains(anchor))) {
                    broken.Add($"{name} -> {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(broken.Count == 0, $"{broken.Count} broken link(s):\n{string.Join("\n", broken.Take(20))}");
        Assert.True(pages.Count > 20, "the library documented to almost nothing");
    }

    [Fact]
    public void CarriesDocCommentsAndCrossLinks() {
        var (_, _, pages) = Library.Value;
        var stack = pages["Sable.Collections.Stack.html"];
        Assert.Contains("Last in, first out.", stack, StringComparison.Ordinal);
        Assert.Contains("<a href=\"Sable.Core.Int.html\">Int</a>", stack, StringComparison.Ordinal);
        Assert.Contains("Sable.Collections.html", pages["index.html"], StringComparison.Ordinal);

        // A conformance an extension adds is part of the type's signature.
        Assert.Contains("<a href=\"Sable.Core.Comparable.html\">Comparable</a>", pages["Sable.Core.Int.html"], StringComparison.Ordinal);
    }

    [Fact]
    public void TheSymbolGraphIsJsonWithAnEdgePerMembership() {
        var (_, set, _) = Library.Value;
        using var document = JsonDocument.Parse(JsonDocWriter.Render(set));
        var root = document.RootElement;
        Assert.Equal(set.Entries.Count, root.GetProperty("symbols").GetArrayLength());
        Assert.Equal(set.Modules.Count, root.GetProperty("modules").GetArrayLength());

        var ids = root.GetProperty("symbols").EnumerateArray().Select(static s => s.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        var memberships = root.GetProperty("relationships").EnumerateArray().Where(static r => r.GetProperty("kind").GetString() == "memberOf").ToList();
        Assert.Equal(set.Entries.Values.Count(static e => e.Parent is not null), memberships.Count);
        Assert.All(memberships, r => Assert.Contains(r.GetProperty("target").GetString(), ids));
    }

    [Fact]
    public void DocumentsAProgramsPublicDeclarationsOnly() {
        const string source = """
            module Shop

            //! Things to buy.

            /// A thing on a shelf.
            public struct Item {
                /// What it costs, in cents.
                public var price: Int
                var secret: Int

                /// Half off.
                public fn discounted() -> Item { return Item { price: price / 2, secret: secret } }
            }

            /// The total of some items.
            public fn total(_ items: Item[>..]) -> Int {
                var sum = 0
                for item in items {
                    sum += item.price
                }
                return sum
            }

            fn hidden() { }

            extension String {
                /// Shouted.
                public fn loud() -> String { return self + "!" }
            }
            """;

        var set = DocCommand.ForSources([SyntaxTree.Parse(new SourceText(source, "shop.sb"))], out var errors);
        Assert.Empty(errors);
        Assert.NotNull(set);

        var module = Assert.Single(set!.Modules);
        Assert.Equal("Things to buy.", module.Doc);
        var item = Assert.Single(module.Types);
        Assert.Equal("A thing on a shelf.", item.Doc);
        Assert.Equal(["price", "discounted"], item.Members.Select(static m => m.Name));
        Assert.Equal("public var price: Int", item.Members[0].Signature);
        Assert.Equal("What it costs, in cents.", item.Members[0].Doc);
        Assert.Equal("public fn total(_ items: Item[>..]) -> Int", Assert.Single(module.Functions).Signature);

        var (type, members) = Assert.Single(module.Extensions);
        Assert.Equal("String", type.Name);
        Assert.Equal("Shouted.", Assert.Single(members).Doc);

        var pages = HtmlDocWriter.Render(set);
        Assert.Contains("<a href=\"Shop.Item.html\">Item</a>[&gt;..]", pages["Shop.html"], StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", pages["Shop.html"], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", pages["Shop.Item.html"], StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramThatDoesNotCompileIsNotDocumented() {
        var set = DocCommand.ForSources([SyntaxTree.Parse(new SourceText("module M\npublic fn f() -> Int { return \"no\" }\n", "m.sb"))], out var errors);
        Assert.Null(set);
        Assert.Contains(errors, static e => e.Contains("S3004", StringComparison.Ordinal));
    }
}
