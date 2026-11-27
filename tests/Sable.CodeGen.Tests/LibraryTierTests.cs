using Sable.Driver;
using Sable.Driver.Packages;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The standard library's tiers (docs/06 §6.4): every module of
///     <c>lib/</c> is core or an extension, as <c>lib/package.sb</c> lists
///     it, and no core module imports an extension one, directly or through
///     another (S3239 where the import is written).
/// </summary>
public sealed class LibraryTierTests {
    static bool IsExtension(string module) => LibraryTiers.TierOf(module) == ModuleTier.Extension;

    [Fact]
    public void EveryModuleOfTheLibraryHasATierAndEveryListedOneIsThere() {
        var listed = LibraryTiers.Modules.Select(static m => m.Key).ToHashSet(StringComparer.Ordinal);
        var modules = Library.ModuleNames.Append(Library.Core).ToHashSet(StringComparer.Ordinal);
        Assert.True(modules.Except(listed).ToList() is [], $"lib/package.sb gives no tier to {string.Join(", ", modules.Except(listed))}");
        Assert.True(listed.Except(modules).ToList() is [], $"lib/package.sb lists {string.Join(", ", listed.Except(modules))}, which lib/ does not have");
        var directories = Directory.GetDirectories(Path.Combine(DocExampleTests.RepositoryRoot, "lib")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.True(directories.SetEquals(listed), $"lib/ has {string.Join(", ", directories.Order())}; lib/package.sb lists {string.Join(", ", listed.Order())}");
    }

    [Fact]
    public void NoCoreModuleImportsAnExtensionOneThroughAnyOther() {
        var broken = LibraryTiers.Modules
            .Where(static m => m.Value == ModuleTier.Core)
            .Select(static m => Library.ImportChain(m.Key, ParseOptions.Default, IsExtension))
            .OfType<IReadOnlyList<string>>()
            .Select(static chain => string.Join(" → ", chain))
            .ToList();
        Assert.True(broken.Count == 0, $"a core module imports an extension:\n{string.Join("\n", broken)}");
    }

    [Fact]
    public void AChainNamesEveryModuleOnTheWay() {
        Assert.Equal(["Sable.Hosting", "Sable.Configuration"], Library.ImportChain("Sable.Hosting", ParseOptions.Default, static m => m == "Sable.Configuration"));
        Assert.Equal(["Sable.Net.Tls", "Sable.Crypto", "Sable.Serialize.Asn1"],
            Library.ImportChain("Sable.Net.Tls", ParseOptions.Default, static m => m == "Sable.Serialize.Asn1"));
        Assert.Null(Library.ImportChain("Sable.Text", ParseOptions.Default, IsExtension));
    }

    /// <summary>The compiler's reading of the list and the manifest's evaluation, through Sable.Build, agree.</summary>
    [Fact]
    public void TheManifestEvaluatedListsTheSameTiers() {
        var manifest = ManifestFiles.Load(Path.Combine(DocExampleTests.RepositoryRoot, "lib"));
        Assert.Empty(manifest.Warnings);
        Assert.Equal(
            LibraryTiers.Modules.Select(static m => (m.Key, m.Value == ModuleTier.Core ? "core" : "extension")),
            manifest.Modules.Select(static m => (m.Key, m.Value)));
    }

    [Theory]
    [InlineData("modules: [.core(\"A\"), .extension(\"B\")]", null)]
    [InlineData("modules: [.core(\"A\"), .core(\"A\")]", "'A' is listed twice")]
    [InlineData("modules: [.core(name)]", "is not `.core(\"name\")`")]
    [InlineData("modules: all", "a literal list")]
    [InlineData("publish: false", "a literal list")]
    public void TheListIsWrittenAsALiteral(string field, string? error) {
        var text = $"import Sable.Build\n\nlet manifest = Package {{\n    name: \"p\",\n    version: \"0.1.0\",\n    {field},\n}}\n";
        if (error is null) {
            Assert.Equal([new("A", ModuleTier.Core), new KeyValuePair<string, ModuleTier>("B", ModuleTier.Extension)], LibraryTiers.Read(text, "package.sb"));
        } else {
            Assert.Contains(error, Assert.Throws<FormatException>(() => LibraryTiers.Read(text, "package.sb")).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACoreModuleThatImportsAnExtensionIsReported() {
        var tree = SyntaxTree.Parse("module Sable.Text\n\nimport Sable.Validation\n", "lib/Sable.Text/Bad.sb");
        var compilation = Compilation.Create([tree, ..Library.For(tree)]);
        var found = compilation.GetDiagnostics().Where(static d => d.Code == ErrorCode.ERR_CoreImportsExtension).ToList();
        var diagnostic = Assert.Single(found);
        Assert.Contains("'Sable.Text' is a core module", diagnostic.Message, StringComparison.Ordinal);

        var allowed = SyntaxTree.Parse("module Sable.Hosting\n\nimport Sable.Validation\n", "lib/Sable.Hosting/Fine.sb");
        Assert.DoesNotContain(Compilation.Create([allowed, ..Library.For(allowed)]).GetDiagnostics(), static d => d.Code == ErrorCode.ERR_CoreImportsExtension);
    }
}
