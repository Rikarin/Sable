using System.Runtime.CompilerServices;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>Evaluated manifests go to a temporary directory, never the real <c>~/.sable</c>.</summary>
static class ManifestCache {
    [ModuleInitializer]
    internal static void Isolate() =>
        ManifestEvaluator.CacheDirectory = Directory.CreateTempSubdirectory("sable-manifests-").FullName;
}
