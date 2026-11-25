using System.Security.Cryptography;
using System.Text;
using Sable.CodeGen;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Build;

/// <summary>
///     A source file of a program: where it is, the name diagnostics give it,
///     the features it is parsed with, for a registry dependency the
///     workspace outside this one it comes from, which <c>package</c> stops
///     at, and the package it belongs to, whose modules are its own.
/// </summary>
public sealed record SourceFile(string Path, string Name, IReadOnlyList<string> Features, string? Workspace = null, string? Package = null) {
    /// <summary>The attributes the package's generators answer to, and those they read on members (docs/08).</summary>
    public GeneratorAttributes Generators { get; init; } = GeneratorAttributes.None;

    public ParseOptions Options => ParseOptions.Default with {
        Features = [..Features],
        Workspace = Workspace,
        Package = Package,
        GeneratorAttributes = [..Generators.Types],
        GeneratorMemberAttributes = [..Generators.Members],
    };
}

/// <summary>The attributes a package's visible source generators answer to on types, and read on members.</summary>
public sealed record GeneratorAttributes(IReadOnlyList<string> Types, IReadOnlyList<string> Members) {
    public static readonly GeneratorAttributes None = new([], []);

    /// <summary>What a cache keyed by a file's options hashes.</summary>
    public override string ToString() => $"{string.Join(",", Types)};{string.Join(",", Members)}";
}

/// <summary>
///     A unit of separate compilation: a package's library, or a binary's
///     entry file. The standard library the program imports is one more,
///     which the build adds.
/// </summary>
public sealed record UnitPlan(string Name, IReadOnlyList<SourceFile> Files) {
    /// <summary>
    ///     For a registry or git package's library, what its entry in the
    ///     shared <see cref="BuildCache" /> is keyed by: built once for every
    ///     project that uses it, as the standard library is. Null for a unit
    ///     of the workspace's own, kept in its <c>target/</c>.
    /// </summary>
    public PackageUnit? Package { get; init; }
}

/// <summary>
///     A dependency's library as a unit the <see cref="BuildCache" /> keeps
///     (#182): its package and version, and the units of the packages it
///     depends on, whose entries' keys are part of its own.
/// </summary>
/// <param name="Dependencies">The names of the units (<see cref="UnitPlan.Name" />) of everything it depends on, directly or not.</param>
public sealed record PackageUnit(string Name, string Version, IReadOnlyList<string> Dependencies);

/// <summary>How a program is built: the profile's settings that change what comes out.</summary>
/// <param name="DebugDirectory">Where debug information's file names are relative to; null for no debug information.</param>
public sealed record BuildSettings(string Profile, bool Optimise, string? DebugDirectory, bool Exclusivity) {
    public bool Debug => DebugDirectory is not null;

    /// <summary>Whether integer overflow traps (<c>overflow-checks</c>); without, the arithmetic wraps.</summary>
    public bool OverflowChecks { get; init; } = true;

    /// <summary>
    ///     What clang is asked for: by default <c>-O2</c> or <c>-O0</c> as
    ///     <see cref="Optimise" /> says, with debug information when there is
    ///     a debug directory; a profile says more.
    /// </summary>
    public NativeOptions Native {
        get => _native ?? NativeOptions.Of(Optimise, Debug);
        init => _native = value;
    }

    readonly NativeOptions? _native;

    /// <summary>
    ///     Separately compiled — each unit to an object of its own, cached —
    ///     when nothing needs to see the whole program at once: an unoptimised
    ///     build, which runs no SIL pass that looks across functions and no
    ///     link-time optimisation; or an optimised build whose profile asks
    ///     for ThinLTO (or none), where each unit's SIL passes run on the unit
    ///     and LLVM optimises across units as they link. A profile's default,
    ///     whole-program link-time optimisation (<c>lto = "fat"</c>), compiles
    ///     the program whole, so that the SIL passes see all of it; so does an
    ///     optimised build with debug information, whose link-time objects
    ///     Apple's debugger would need kept. Otherwise only an unchanged build
    ///     is skipped.
    /// </summary>
    public bool Separate =>
        !Whole && (!Optimise || !Debug && Native.Lto != LtoMode.Fat)
        && (Library is not { } kind || NativeObjects.LinksLibrary(kind, Native.TargetOrHost));

    /// <summary>
    ///     The settings that change a library's compiled code, each
    ///     <c>name=value</c>: what its <see cref="BuildCache" /> entry is keyed
    ///     by besides its source, target and compiler. Not the profile's name,
    ///     where its output goes or how the program is linked.
    /// </summary>
    public IReadOnlyList<string> CodeOptions => [
        $"optimise={Optimise}",
        $"sil={SilLevel}",
        $"debug={Debug}",
        $"exclusivity={Exclusivity}",
        $"overflow={OverflowChecks}",
        $"arc={ArcChecks}",
        $"deny-reflection={DenyReflection}",
        $"debug-assertions={DebugAssertions}",
        $"generics={Sable.SIL.SilGen.Generics}",
        $"optimised-program={OptimisedProgram}",
        $"clang={string.Join(' ', [..NativeObjects.Flags(Native), ..NativeBuild.PositionIndependentFlags(Native)])}",
        $"cc={CacheKey.Checksum(NativeObjects.CompilerIdentity)}",
    ];

    /// <summary>
    ///     Build a library C links rather than an executable: the first unit
    ///     leads it, with no entry point — it sets up the globals as the
    ///     library is loaded (<see cref="SilGen.LowerUnit" />'s
    ///     <c>library</c>). Compiled separately where the target's link can
    ///     make one of objects (<see cref="NativeObjects.LinksLibrary" />).
    /// </summary>
    public LibraryKind? Library { get; init; }

    /// <summary>Compile the program whole even where it could be compiled separately: what a separate build is checked against.</summary>
    public bool Whole { get; init; }

    /// <summary>Built without reflection (docs/06 §6.1): <c>Mirror</c> and <c>TypeInfo</c> are refused, and field descriptors have no names.</summary>
    public bool DenyReflection { get; init; }

    /// <summary>A profile's <c>arc-checks</c>: compiled code's retains and releases trap on an object whose deinit has run.</summary>
    public bool ArcChecks { get; init; }

    /// <summary>
    ///     Whether the program's files and the library's are parsed with
    ///     <c>DEBUG</c> defined rather than <c>RELEASE</c> (SYNTAX.md §21): a
    ///     profile's <c>debug-assertions</c>, or else an unoptimised build.
    /// </summary>
    public bool DebugAssertions {
        get => _debugAssertions ?? !Optimise;
        init => _debugAssertions = value;
    }

    readonly bool? _debugAssertions;

    /// <summary>
    ///     The opt-level whose SIL passes run (<see cref="SilPassManager.ForLevel" />):
    ///     the profile's, or <c>2</c> for an optimised build whose native
    ///     options do not say, and <c>0</c> for an unoptimised one.
    /// </summary>
    public string SilLevel => !Optimise ? "0" : Native.Optimise ? Native.OptLevel : "2";

    /// <summary>
    ///     The settings a library's unit — the standard library's, and a
    ///     registry or git dependency's — is compiled with (docs/05 §5.4,
    ///     "Libraries compiled optimised"): optimised in every profile, as
    ///     Swift ships its standard library. An optimised build's are its
    ///     own. An unoptimised one's are <see cref="LibraryOptLevel" />'s
    ///     SIL passes and clang's, to a plain object (no link-time
    ///     optimisation, which an unoptimised link does not do), with the
    ///     debug information the build has. Every check the program's units
    ///     are built with stays as the profile says: exclusivity, overflow,
    ///     ARC, and <c>DEBUG</c>, and with it <c>assert</c>.
    /// </summary>
    public BuildSettings ForLibrary => Optimise ? this : this with {
        Optimise = true,
        OptimisedProgram = false,
        DebugAssertions = DebugAssertions,
        Native = Native with { OptLevel = LibraryOptLevel, Lto = LtoMode.Off },
    };

    /// <summary>Whether the program is optimised, whatever a library's own unit is (<see cref="Sable.SIL.SilGen.UseOptimised" />): its units agree on it.</summary>
    public bool OptimisedProgram {
        get => _optimisedProgram ?? Optimise;
        init => _optimisedProgram = value;
    }

    readonly bool? _optimisedProgram;

    /// <summary>
    ///     The opt-level a library's unit is compiled at in an unoptimised
    ///     build: <c>s</c>, which was as fast as <c>2</c> and smaller
    ///     (docs/05 §5.4).
    /// </summary>
    public const string LibraryOptLevel = "s";
}

/// <summary>What a build did.</summary>
/// <param name="Kind"><c>fresh</c> (nothing to do), <c>incremental</c> or <c>whole</c>.</param>
public sealed record BuildOutcome(bool Succeeded, IReadOnlyList<string> Errors, string Kind, int Compiled, int Reused) {
    public static BuildOutcome Failed(IReadOnlyList<string> errors) => new(false, errors, "failed", 0, 0);

    /// <summary>The front end's warnings about the files it checked: none when nothing was compiled again.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    ///     Why a program that could be compiled in units was compiled whole
    ///     after all — a unit that could not be lowered alone, units that
    ///     disagree about a type, units that did not link — with what the
    ///     step that failed said; null when nothing fell back. Never silent:
    ///     the build's log says it too.
    /// </summary>
    public string? FellBack { get; init; }
}

/// <summary>
///     Builds a program of many files into an executable, doing again only
///     what its inputs' changes call for (docs/05 §5.4).
/// </summary>
/// <remarks>
///     <para>
///         <b>The build cache</b>, in <c>target/&lt;profile&gt;/cache</c>. Every
///         key is a SHA-256 over the toolchain (a hash of the compiler's own
///         assemblies, which carry the standard library, and what
///         <c>clang --version</c> says), the settings, the target, and the
///         content of what is compiled — file contents and names and the
///         features each is parsed with, never timestamps.
///     </para>
///     <para>
///         <b>An unchanged build</b> is one whose key over every input is the
///         one the executable was last built from, with the executable as it
///         was left; it reads and hashes the files and does nothing else.
///     </para>
///     <para>
///         <b>Separate compilation.</b> Each unit is lowered from a compilation
///         of the whole program (<see cref="SilGen.LowerUnit" />) to an object
///         file keyed by its own files' contents and the interfaces
///         (<see cref="InterfaceFingerprint" />) of the units it depends on
///         (<see cref="UnitGraph" />). A change to an ordinary function's body
///         changes only its unit's key; a change to what other units can see
///         changes the keys of the units that depend on it — and an
///         extension's, every key, since any unit may see it. Units whose objects
///         are cached are neither type-checked again (their checks passed with
///         the same inputs) nor lowered; the others are, and their objects are
///         compiled by clang in parallel. Before linking, the units' records of
///         the program's types must agree; if they do not, or a unit's lowering
///         reports what the whole program's would not, or the link fails, the
///         program is built whole instead — a stale or inconsistent object never
///         reaches an executable.
///     </para>
/// </remarks>
public static class IncrementalBuild {
    /// <summary>
    ///     The toolchain's identity: the compiler's own (<see cref="CompilerIdentity" />:
    ///     its assemblies, the library and runtime they embed, its version),
    ///     the C compiler's version, and the machine's.
    /// </summary>
    public static string Toolchain => ToolchainHash.Value;

    static readonly Lazy<string> ToolchainHash = new(() => {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(CompilerIdentity.Current));
        hash.AppendData(Encoding.UTF8.GetBytes(NativeObjects.CompilerIdentity));
        hash.AppendData(Encoding.UTF8.GetBytes($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}|{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    });

    /// <summary>Format version of the cache: bump when what is cached, or how it is keyed, changes meaning.</summary>
    const string CacheVersion = "sable-build-cache-1";

    /// <summary>
    ///     Builds <paramref name="units" /> — the first holding the entry point,
    ///     or leading the library <see cref="BuildSettings.Library" /> asks for —
    ///     into <paramref name="executable" />, keeping what can be kept in
    ///     <paramref name="cacheDirectory" />.
    /// </summary>
    /// <param name="cache">
    ///     The build cache every project shares, where the runtime's and the
    ///     standard library's archives are kept: the user's when not given.
    /// </param>
    public static BuildOutcome Build(IReadOnlyList<UnitPlan> units, BuildSettings settings, string executable, string cacheDirectory, BuildTimings? timings = null, TextWriter? log = null, BuildCache? cache = null) {
        units = [..units.Where(static u => u.Files.Count > 0)];
        cache ??= BuildCache.Default;
        using (BuildTimings.Of(timings, "toolchain")) {
            _ = Toolchain;
        }

        var settingsKey = $"{CacheVersion}|{Toolchain}|{settings.Profile}|opt={settings.Optimise}|debug={settings.DebugDirectory}|exclusivity={settings.Exclusivity}|overflow={settings.OverflowChecks}|whole={settings.Whole}|deny-reflection={settings.DenyReflection}|arc={settings.ArcChecks}|sil={settings.SilLevel}|debug-assertions={settings.DebugAssertions}|native={settings.Native.Key}|library={settings.Library}|generics={SilGen.Generics}";

        // ---- Read and hash every file --------------------------------------
        string[][] texts;
        string[][] fileHashes;
        using (BuildTimings.Of(timings, "fingerprint")) {
            texts = [..units.Select(static u => new string[u.Files.Count])];
            fileHashes = [..units.Select(static u => new string[u.Files.Count])];
            var all = units.SelectMany(static (u, i) => u.Files.Select((f, j) => (i, j, f))).ToList();
            Parallel.ForEach(all, GlobalOptions.Current.ParallelOptions, item => {
                var (i, j, file) = item;
                var text = File.ReadAllText(file.Path);
                texts[i][j] = text;
                fileHashes[i][j] = Hash($"{file.Name}\n{string.Join(",", file.Features.Order(StringComparer.Ordinal))}\n{file.Generators}\n{text}");
            });
        }

        var buildKey = Hash(settingsKey + "\n" + string.Join("\n", units.Select((u, i) => $"{u.Name}:{string.Join(",", fileHashes[i])}")));
        var record = Path.Combine(cacheDirectory, "builds", Sanitise(Path.GetFileName(executable)) + ".txt");
        if (IsFresh(record, buildKey, executable)) {
            return new(true, [], "fresh", 0, units.Count + 1);
        }

        // ---- Parse, for the target ------------------------------------------
        // Separately, a unit whose .sbi is there is parsed from it: what other
        // units bind and lower against, its bodies blanked. Only a unit then
        // found to need compiling again is parsed from its source.
        var target = settings.Native.TargetOrHost;
        // 'DEBUG' or 'RELEASE' follows from the settings, which key the cache.
        var sources = new ProgramSources(units, texts, file => Pipeline.ParseOptionsFor(!settings.DebugAssertions, file.Options.ForTarget(target.Os, target.Arch)));
        var known = new InterfaceFile?[units.Count];
        using (BuildTimings.Of(timings, "parse")) {
            if (settings.Separate) {
                var interfaces = Path.Combine(cacheDirectory, "interfaces");
                Parallel.For(0, units.Count, GlobalOptions.Current.ParallelOptions, i => {
                    IReadOnlyList<string> names = [..units[i].Files.Select(static f => f.Name)];
                    known[i] = InterfaceFile.Read(InterfacePath(interfaces, settingsKey, units[i].Name, fileHashes[i]), units[i].Name, names);

                    // A dependency's, which another project may have worked
                    // out already: from the shared cache.
                    if (known[i] is null && SharedInterface(cache, units[i], fileHashes[i], settings) is { } shared) {
                        known[i] = InterfaceFile.Read(shared, units[i].Name, names);
                    }
                });
            }

            sources.Parse(known);
            timings?.Count("files parsed", sources.Parsed);
            timings?.Count("files parsed from interfaces", sources.FromInterfaces);
        }

        IReadOnlyList<SyntaxTree> library;
        using (BuildTimings.Of(timings, "library source")) {
            library = Library.For(sources.Program);
            CoreLibrary.TreesFor(sources.Trees[0][0].Options);
        }

        var outcome = settings.Separate
            ? Separately(sources, known, fileHashes, library, settings, settingsKey, executable, cacheDirectory, cache, timings, log)
            : Whole(sources.Program, settings, executable, cacheDirectory, cache, timings);

        if (outcome.Succeeded) {
            using var _ = BuildTimings.Of(timings, "record");
            Record(record, buildKey, executable);
        }

        return outcome;
    }

    /// <summary>
    ///     Where a dependency's unit's interface file (<see cref="InterfaceFile" />)
    ///     is kept in the shared cache, an entry of its own keyed by its
    ///     source and what it is parsed for — not by the program it is in, as
    ///     its archive's entry is — so that every project finds it; null for
    ///     a unit of the workspace's own.
    /// </summary>
    static string? SharedInterface(BuildCache cache, UnitPlan unit, IEnumerable<string> fileHashes, BuildSettings settings) =>
        unit.Package is { } package ? Path.Combine(cache.DirectoryFor(SharedInterfaceKey(cache, package, fileHashes, settings)), SharedInterfaceFile) : null;

    const string SharedInterfaceFile = "unit.sbi";

    static CacheKey SharedInterfaceKey(BuildCache cache, PackageUnit package, IEnumerable<string> fileHashes, BuildSettings settings) =>
        cache.Key("interface", package.Name, package.Version, UnitContent(fileHashes), settings.Native.TargetOrHost.Triple, [$"debug-assertions={settings.DebugAssertions}"]);

    /// <summary>Where a unit's <c>.sbi</c> is kept: under its content and the build's settings.</summary>
    static string InterfacePath(string interfaces, string settingsKey, string unit, IEnumerable<string> fileHashes) =>
        Path.Combine(interfaces, Hash($"{settingsKey}\ninterface {unit}\n{UnitContent(fileHashes)}") + ".sbi");

    static string UnitContent(IEnumerable<string> fileHashes) => Hash(string.Join("\n", fileHashes));

    /// <summary>
    ///     The program's trees, each unit's parsed from its source or, where
    ///     its <c>.sbi</c> was found, from its interface — and from its source
    ///     again once that is needed: to compile the unit, to work its
    ///     interface out, or to compile the program whole.
    /// </summary>
    sealed class ProgramSources(IReadOnlyList<UnitPlan> plans, string[][] texts, Func<SourceFile, ParseOptions> options) {
        public IReadOnlyList<UnitPlan> Plans => plans;

        public SyntaxTree[][] Trees { get; } = [..plans.Select(static p => new SyntaxTree[p.Files.Count])];

        readonly bool[] _fromInterface = new bool[plans.Count];

        public int Parsed;

        public int FromInterfaces => _fromInterface.Select((f, i) => f ? plans[i].Files.Count : 0).Sum();

        public List<SyntaxTree> Program => [..Trees.SelectMany(static t => t)];

        public void Parse(IReadOnlyList<InterfaceFile?> known) {
            var all = plans.SelectMany(static (p, i) => p.Files.Select((f, j) => (i, j, f))).ToList();
            for (var i = 0; i < plans.Count; i++) {
                _fromInterface[i] = known[i] is not null;
            }

            Parallel.ForEach(all, GlobalOptions.Current.ParallelOptions, item => {
                var (i, j, file) = item;
                var text = known[i] is { } face ? face.Files[j].Interface.Source : texts[i][j];
                Trees[i][j] = SyntaxTree.Parse(new SourceText(text, file.Name), options(file));
            });
            Parsed += all.Count;
        }

        /// <summary>A unit's trees parsed from its source, as they are or parsed now.</summary>
        public IReadOnlyList<SyntaxTree> Full(int unit) {
            if (_fromInterface[unit]) {
                var files = plans[unit].Files;
                Parallel.For(0, files.Count, GlobalOptions.Current.ParallelOptions, j =>
                    Trees[unit][j] = SyntaxTree.Parse(new SourceText(texts[unit][j], files[j].Name), options(files[j])));
                Interlocked.Add(ref Parsed, files.Count);
                _fromInterface[unit] = false;
            }

            return Trees[unit];
        }

        /// <summary>The whole program from its sources.</summary>
        public List<SyntaxTree> Whole() {
            for (var i = 0; i < plans.Count; i++) {
                Full(i);
            }

            return Program;
        }
    }

    // =======================================================================
    // An unchanged build
    // =======================================================================

    static bool IsFresh(string record, string buildKey, string executable) {
        if (!File.Exists(record) || !File.Exists(executable)) {
            return false;
        }

        var lines = File.ReadAllLines(record);
        if (lines is not [var key, var length, var written, var hash] || key != buildKey) {
            return false;
        }

        // The executable as it was left: its size and time, or failing those its content.
        var info = new FileInfo(executable);
        if (info.Length.ToString() == length && info.LastWriteTimeUtc.Ticks.ToString() == written) {
            return true;
        }

        return info.Length.ToString() == length && HashFile(executable) == hash;
    }

    static void Record(string record, string buildKey, string executable) {
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        var info = new FileInfo(executable);
        File.WriteAllLines(record, [buildKey, info.Length.ToString(), info.LastWriteTimeUtc.Ticks.ToString(), HashFile(executable)]);
    }

    // =======================================================================
    // The whole program
    // =======================================================================

    static BuildOutcome Whole(IReadOnlyList<SyntaxTree> program, BuildSettings settings, string executable, string cacheDirectory, BuildCache cache, BuildTimings? timings) {
        var result = Pipeline.Compile(program, settings.DebugDirectory, settings.Exclusivity, timings, optimised: settings.Optimise, target: settings.Native.TargetOrHost, checkOverflow: settings.OverflowChecks, denyReflection: settings.DenyReflection, optLevel: settings.SilLevel, checkArc: settings.ArcChecks, library: settings.Library is not null);
        if (!result.Succeeded) {
            return BuildOutcome.Failed(result.Errors) with { Warnings = result.Warnings };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        NativeBuildResult native;
        using (BuildTimings.Of(timings, "clang")) {
            native = settings.Library is { } kind
                ? NativeBuild.BuildLibrary(result.LlvmIr!, executable, kind, settings.Native, cache)
                : NativeBuild.Build(result.LlvmIr!, executable, settings.Native, cache);
        }

        return (native.Succeeded
            ? new(true, [], "whole", 1, 0)
            : BuildOutcome.Failed([native.Report])) with { Warnings = result.Warnings };
    }

    // =======================================================================
    // Separately
    // =======================================================================

    /// <param name="library">
    ///     A library's unit — the standard library's, or a registry or git
    ///     dependency's — whose compiled code is kept in the shared
    ///     <see cref="BuildCache" /> as an archive (<see cref="LibraryUnit" />)
    ///     that programs link against, rather than as an object of this program's.
    /// </param>
    sealed class Unit(string name, IReadOnlyList<SyntaxTree> trees, string contentHash, bool library = false) {
        public string Name { get; } = name;
        public IReadOnlyList<SyntaxTree> Trees { get; set; } = trees;
        public string ContentHash { get; } = contentHash;
        public string Key { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string AbiPath { get; set; } = "";
        public bool Cached { get; set; }

        /// <summary>For a library's unit, where its object, archive and records are.</summary>
        public LibraryUnit? Library { get; set; }

        public bool IsLibrary { get; } = library;

        /// <summary>A library unit's key in the shared cache.</summary>
        public CacheKey? Entry { get; set; }

        /// <summary>What a library unit's files in its entry are named: <c>std</c>, or the package's name.</summary>
        public string LibraryName { get; init; } = "std";

        /// <summary>What a program links: an ordinary unit's object, or a library's archive.</summary>
        public string Linked => Library?.Archive ?? ObjectPath;
    }

    static BuildOutcome Separately(
        ProgramSources sources, IReadOnlyList<InterfaceFile?> known, string[][] fileHashes, IReadOnlyList<SyntaxTree> library, BuildSettings settings,
        string settingsKey, string executable, string cacheDirectory, BuildCache cache, BuildTimings? timings, TextWriter? log
    ) {
        var objects = Path.Combine(cacheDirectory, "objects");
        Directory.CreateDirectory(objects);

        // The units: the program's, then the standard library's — the core,
        // which every compilation has, and the modules the program imports.
        var plans = sources.Plans;
        var own = sources.Trees.Select((t, i) => new Unit(plans[i].Name, t, UnitContent(fileHashes[i]), library: plans[i].Package is not null) {
            LibraryName = plans[i].Package is { } package ? Sanitise(package.Name).Replace('-', '_') : "std",
        }).ToList();
        var units = own.ToList();
        var standard = new Unit("std", [..library, ..CoreLibrary.TreesFor(sources.Trees[0][0].Options)], Hash(string.Join("\n", library.Select(static t => t.Text.FileName))), library: true);
        units.Add(standard);

        // ---- Each unit's interface ---------------------------------------------
        // Read from the unit's .sbi when the unit is as it was, and so is
        // which of the types its extensions extend the program declares
        // concretely; otherwise worked out from the unit's source and the .sbi written.
        var faces = new FileInterface[own.Count][];
        var interfaces = Path.Combine(cacheDirectory, "interfaces");
        var interfaceFiles = new string[own.Count];
        using (BuildTimings.Of(timings, "interface")) {
            // Only the names the program's extensions extend are asked about: a
            // file of the standard library that has none of them is not read.
            var extendedNames = own.SelectMany(static u => u.Trees).SelectMany(InterfaceFingerprint.ExtendedNames).ToHashSet(StringComparer.Ordinal);
            var naming = standard.Trees.Where(t => extendedNames.Any(name => t.Text.Text.Contains(name, StringComparison.Ordinal))).ToList();
            SyntaxTree.ParseAll(naming);
            var concrete = InterfaceFingerprint.ConcreteTypeNames([..own.SelectMany(static u => u.Trees), ..naming]);
            var read = 0;
            Parallel.For(0, own.Count, GlobalOptions.Current.ParallelOptions, i => {
                var unit = own[i];
                IReadOnlyList<string> extended = [..unit.Trees.SelectMany(InterfaceFingerprint.ExtendedNames).Where(concrete.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                interfaceFiles[i] = InterfacePath(interfaces, settingsKey, unit.Name, fileHashes[i]);
                if (known[i] is { } file && file.Assumed.SequenceEqual(extended)) {
                    faces[i] = [..file.Files.Select(static f => f.Interface)];
                    Interlocked.Increment(ref read);
                    return;
                }

                unit.Trees = sources.Full(i);
                faces[i] = [..unit.Trees.AsParallel().AsOrdered().Select(tree => InterfaceFingerprint.Interface(tree, concrete))];
                var written = new InterfaceFile(unit.Name, extended, [..unit.Trees.Select(static t => t.Text.FileName).Zip(faces[i])]);
                written.WriteTo(interfaceFiles[i]);
                if (plans[i].Package is { } package) {
                    // For every project that uses the dependency: written
                    // whole, once, as the cache's entries are.
                    cache.GetOrCreate(SharedInterfaceKey(cache, package, fileHashes[i], settings), settings.Profile, temporary => {
                        written.WriteTo(Path.Combine(temporary, SharedInterfaceFile));
                        return new(true, "");
                    });
                }
            });
            timings?.Count("interfaces read", read);
        }

        // ---- Each unit's key -------------------------------------------------
        // Its files, and the interfaces of the units it depends on — not the
        // whole program's (UnitGraph) — and every unit's extensions, which
        // any unit may see. The standard library depends on no unit of the
        // program, and every unit on it.
        CacheKey? standardEntry = null;
        var ownExtensions = "none";
        using (BuildTimings.Of(timings, "unit graph")) {
            var facts = own.Select((u, i) => {
                var declared = u.Trees.Select(UnitFacts.Of).ToList();
                return new UnitFacts(
                    declared.Select(static d => d.Module).OfType<string>().ToHashSet(StringComparer.Ordinal),
                    declared.SelectMany(static d => d.Imports).ToHashSet(StringComparer.Ordinal),
                    faces[i].SelectMany(static f => f.References).ToHashSet(StringComparer.Ordinal));
            }).ToList();
            var dependencies = UnitGraph.Dependencies(facts);
            var unitInterfaces = own.Select((u, i) => Hash(string.Join("\n", u.Trees.Select((t, j) => t.Text.FileName + "=" + faces[i][j].Fingerprint)))).ToList();
            var extensions = Hash(string.Join("\n", faces.SelectMany(static f => f).Select(static f => f.Extensions)));
            standard.Key = Hash($"{settingsKey}\nunit {standard.Name}\n{standard.ContentHash}\n{extensions}");
            ownExtensions = OwnExtensions(faces);
            standardEntry = StandardLibraryKey(cache, settings.ForLibrary, standard.Trees, ownExtensions);
            standard.Entry = standardEntry;

            // A dependency's entry: its source, and the entries it was
            // compiled against — the standard library's, and its own
            // dependencies', each worked out before it (they come after it).
            var byName = own.Select(static (u, i) => (u.Name, i)).ToDictionary(static p => p.Name, static p => p.i, StringComparer.Ordinal);
            for (var i = own.Count - 1; i >= 0; i--) {
                if (plans[i].Package is not { } package) {
                    continue;
                }

                var features = plans[i].Files.SelectMany(static f => f.Features).Distinct(StringComparer.Ordinal).ToList();
                own[i].Entry = cache.Key("library", package.Name, package.Version, own[i].ContentHash, settings.Native.TargetOrHost.Triple,
                    [..settings.ForLibrary.CodeOptions, $"extensions={ownExtensions}"], features,
                    [standardEntry.Hash, ..package.Dependencies.Select(d => own[byName[d]].Entry!.Hash)]);
            }

            // Every unit's declarations by the name each declares, once.
            var declared = new Dictionary<string, List<(int Unit, DeclarationPrint Print)>>(StringComparer.Ordinal);
            for (var d = 0; d < own.Count; d++) {
                foreach (var print in faces[d].SelectMany(static f => f.Declarations)) {
                    if (!declared.TryGetValue(print.Name, out var list)) {
                        declared[print.Name] = list = [];
                    }

                    list.Add((d, print));
                }
            }

            var used = 0;
            for (var i = 0; i < own.Count; i++) {
                var seen = DependencyKey(i, dependencies[i], own, facts, faces, unitInterfaces, declared, ref used);
                own[i].Key = Hash($"{settingsKey}\nunit {own[i].Name}\n{own[i].ContentHash}\n{extensions}\n{standard.ContentHash}\n{seen}");
            }

            timings?.Count("declarations keyed by name", used);
        }

        foreach (var unit in units) {
            if (unit.IsLibrary) {
                unit.Library = LibraryUnit.In(cache, unit.Entry!, settings.Profile, unit.LibraryName, settings.Native.TargetOrHost);
                (unit.ObjectPath, unit.AbiPath, unit.Cached) = (unit.Library.Object, unit.Library.Abi, unit.Library.Exists);
            } else {
                unit.ObjectPath = Path.Combine(objects, unit.Key + ".o");
                unit.AbiPath = Path.ChangeExtension(unit.ObjectPath, ".abi");
                unit.Cached = File.Exists(unit.ObjectPath) && File.Exists(unit.AbiPath);
            }
        }

        var stale = units.Where(static u => !u.Cached).ToList();
        timings?.Count("units reused", units.Count - stale.Count);
        timings?.Count("units compiled", stale.Count);

        // A unit compiled again is checked and lowered from its source; the
        // others are bound from their interfaces, which is all of them that
        // another unit sees.
        using (BuildTimings.Of(timings, "parse (compiled units)")) {
            Parallel.For(0, own.Count, GlobalOptions.Current.ParallelOptions, i => {
                if (!own[i].Cached) {
                    own[i].Trees = sources.Full(i);
                }
            });
        }

        var program = sources.Program;

        // The runtime, as an archive of its own: bitcode where the link
        // optimises across units, so that its small functions inline.
        var target = settings.Native.TargetOrHost;
        var runtimeTask = Task.Run(() => NativeBuild.Runtime(settings.Native, NativeBuild.LtoFlags(settings.Native, target.Platform, NativeBuild.LinksBitcodeFor(target)), cache, settings.Profile));

        var compiling = new List<(Unit Unit, Task<NativeBuildResult> Task)>();
        // '-j' bounds how many clang processes run at once.
        var throttle = new SemaphoreSlim(GlobalOptions.Current.Parallelism);
        List<string> warnings = [];
        if (stale.Count > 0) {
            Compilation compilation;
            List<string> errors;
            using (BuildTimings.Of(timings, "bind")) {
                using (BuildTimings.Of(timings, "bind: declare")) {
                    compilation = Compilation.Create([..program, ..library], library: SymbolsOf(standard));
                }

                using (BuildTimings.Of(timings, "bind: signatures")) {
                    compilation.GetDiagnostics();
                }

                var checking = stale.SelectMany(static u => u.Trees).ToHashSet();
                errors = Diagnostics(compilation, program, checking, DiagnosticSeverity.Error);

                // The program's own: the standard library's are not the program's to act on.
                checking.IntersectWith(program);
                warnings = Diagnostics(compilation, program, checking, DiagnosticSeverity.Warning);
            }

            if (errors.Count > 0) {
                return BuildOutcome.Failed(errors) with { Warnings = warnings };
            }

            var index = timings is null ? SilProgram.Index(compilation) : timings.Measure("index", () => SilProgram.Index(compilation));
            // A library's files are named the same in every program, and
            // their text goes in its debug information: the standard
            // library's, and the dependencies' compiled now.
            var libraries = units.Where(static u => u.IsLibrary).SelectMany(static u => u.Trees).ToList();
            var embedded = Pipeline.EmbeddedSources(libraries, program.Except(libraries));

            // The libraries first — the standard library, then each
            // dependency after those it depends on, which come after it — so
            // that each unit after calls their generic code compiled once in
            // their archives rather than lowering it again (#297 stage 7). A
            // kept library's is read from its interface.
            var compiled = new Dictionary<Unit, LibraryInterface>();
            if (SilGen.Generics == GenericsMode.Unspecialised) {
                using var _ = BuildTimings.Of(timings, "library generics");
                foreach (var unit in units.Where(static u => u.IsLibrary && u.Cached)) {
                    if (CompiledInterfaceOf(unit) is { } found) {
                        compiled[unit] = found;
                    }
                }
            }

            stale = [..stale.Where(static u => u.IsLibrary).Reverse(), ..stale.Where(static u => !u.IsLibrary)];
            foreach (var unit in stale) {
                var leads = settings.Library is not null && ReferenceEquals(unit, own[0]);
                IReadOnlyList<LibraryInterface> before = [..compiled.Where(c => !ReferenceEquals(c.Key, unit)).Select(static c => c.Value)];
                var lowered = Lower(compilation, index, unit.Trees, unit.IsLibrary, leads, unit.IsLibrary ? settings.ForLibrary : settings, embedded, timings, before);

                if (lowered.Alone) {
                    // A unit sees less than the whole program: what it reports,
                    // the whole program's compilation says authoritatively.
                    return FallBack($"unit '{unit.Name}' could not be lowered on its own", string.Join("\n", lowered.Errors));
                }

                if (lowered.Errors.Count > 0) {
                    return BuildOutcome.Failed(lowered.Errors);
                }

                var (ir, abi) = (lowered.Ir!, lowered.Abi);
                var built = unit.Library is null ? null : InterfaceOf(compilation, unit.LibraryName, unit.Trees, lowered, settings, program[0].Options);
                if (built is { Generics.Count: > 0 }) {
                    compiled[unit] = built;
                }

                var face = InterfaceFiles(unit.Library, built, ReferenceEquals(unit, standard) ? library : null, program[0].Options);
                compiling.Add((unit, Task.Run(async () => {
                    await throttle.WaitAsync();
                    try {
                        var started = System.Diagnostics.Stopwatch.GetTimestamp();
                        NativeBuildResult result;
                        if (unit.Library is { } library) {
                            result = library.Write(abi, temporary => NativeObjects.CompileIr(ir, temporary, settings.ForLibrary.Native), settings.Native.TargetOrHost,
                                face);
                        } else {
                            File.WriteAllText(unit.AbiPath, abi);
                            result = NativeObjects.CompileIr(ir, unit.ObjectPath, settings.Native);
                        }

                        timings?.Add("clang (units, summed)", System.Diagnostics.Stopwatch.GetElapsedTime(started));
                        return result;
                    } finally {
                        throttle.Release();
                    }
                })));
            }
        }

        using (BuildTimings.Of(timings, "clang (wait)")) {
            Task.WaitAll([..compiling.Select(static c => c.Task), runtimeTask]);
        }

        foreach (var (unit, task) in compiling) {
            if (!task.Result.Succeeded) {
                return BuildOutcome.Failed([$"internal error: compiling unit '{unit.Name}' failed:\n{task.Result.Output}"]);
            }
        }

        var runtime = runtimeTask.Result;
        if (!runtime.Result.Succeeded) {
            var failed = NativeBuild.Explained(runtime.Result, settings.Native);
            return BuildOutcome.Failed([failed.Setup ? failed.Output : $"internal error: compiling the runtime failed:\n{failed.Output}"]);
        }

        // ---- Every unit must have seen the same types -----------------------
        if ((timings is null ? Disagreement(units) : timings.Measure("abi check", () => Disagreement(units))) is { } disagreement) {
            return FallBack($"the units disagree about {disagreement}", "");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        NativeBuildResult link;
        using (BuildTimings.Of(timings, "link")) {
            // The program's units' objects, then the libraries' archives, the
            // runtime's last: a linker takes from an archive only the members
            // what came before it needs. A static library C links is the
            // objects joined whole (-r), the libraries' and the runtime's too.
            link = settings.Library is LibraryKind.Static
                ? NativeObjects.LinkLibrary([..units.Select(static u => u.ObjectPath), runtime.Object], executable, LibraryKind.Static, settings.Native, Path.Combine(cacheDirectory, "thinlto"))
                : settings.Library is { } kind
                    ? NativeObjects.LinkLibrary([..units.Select(static u => u.Linked), runtime.Archive], executable, kind, settings.Native, Path.Combine(cacheDirectory, "thinlto"))
                    : NativeObjects.Link([..units.Select(static u => u.Linked), runtime.Archive], executable, settings.Native, Path.Combine(cacheDirectory, "thinlto"));
        }

        if (!link.Succeeded) {
            return FallBack($"the units did not link ({FirstError(link.Output)})", link.Output);
        }

        timings?.Count("library files parsed", standard.Trees.Count(static t => t.IsParsed));
        using (BuildTimings.Of(timings, "cache upkeep")) {
            Collect(objects, units);
            CollectInterfaces(interfaces, interfaceFiles);

            // The standard library's entry, with its interface, for a
            // program that extends nothing: proof that the library checks
            // cleanly, which a fresh process then need not learn again.
            if (ownExtensions == "none" && File.Exists(standard.Library!.Interface)) {
                Library.MarkChecked(standard.Trees, program[0].Options, cache.Root);
            }
        }

        return new(true, [], "incremental", stale.Count, units.Count - stale.Count) { Warnings = warnings };

        // The program compiled whole instead, saying why: in the log, and in
        // the outcome with all that the step that failed said.
        BuildOutcome FallBack(string reason, string said) {
            log?.WriteLine($"note: {reason}; compiling the program whole");
            var whole = Whole(sources.Whole(), settings, executable, cacheDirectory, cache, timings);
            return whole with { FellBack = said.Length == 0 ? reason : $"{reason}:\n{said.TrimEnd()}" };
        }
    }

    /// <summary>
    ///     What of its dependencies a unit's key covers (docs/05 §5.4,
    ///     "Dependencies by name"). The unit holding the entry point sets up
    ///     every global, and a unit sharing a module with another sees all of
    ///     it as its own: those take a dependency's whole interface. Any other
    ///     takes, of each dependency, its modules and the fingerprints of the
    ///     top-level declarations it may use — those named as the unit writes
    ///     a name anywhere in its files, those every unit may use (an
    ///     operator, a global), and, closed over, those named in the
    ///     interfaces of what it uses (a function's result type, a generic
    ///     body's callees). So a declaration added, changed or removed in a
    ///     package compiles again only the units that write its name or reach
    ///     it, rather than every unit depending on the package.
    /// </summary>
    /// <param name="used">Counts the declarations keyed by name, for the timings.</param>
    static string DependencyKey(
        int unit, IReadOnlyList<int> dependencies, IReadOnlyList<Unit> units, IReadOnlyList<UnitFacts> facts, IReadOnlyList<FileInterface[]> faces,
        IReadOnlyList<string> interfaces, IReadOnlyDictionary<string, List<(int Unit, DeclarationPrint Print)>> declared, ref int used
    ) {
        var whole = new List<int>();
        var byName = new List<int>();
        foreach (var d in dependencies) {
            (unit == 0 || facts[unit].Modules.Overlaps(facts[d].Modules) ? whole : byName).Add(d);
        }

        var parts = whole.Select(d => $"{units[d].Name}:{interfaces[d]}").ToList();
        if (byName.Count == 0) {
            return string.Join("\n", parts);
        }

        // What the unit writes, and what that reaches of the dependencies
        // keyed by name, until nothing more is named.
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        void Need(string name) {
            if (needed.Add(name)) {
                pending.Enqueue(name);
            }
        }

        Need(DeclarationPrint.Everyone);
        foreach (var name in faces[unit].SelectMany(static f => f.Uses)) {
            Need(name);
        }

        var selected = byName.ToDictionary(static d => d, static _ => new List<DeclarationPrint>());
        while (pending.TryDequeue(out var name)) {
            if (!declared.TryGetValue(name, out var prints)) {
                continue;
            }

            foreach (var (d, print) in prints) {
                if (!selected.TryGetValue(d, out var chosen)) {
                    continue;
                }

                chosen.Add(print);
                foreach (var mention in print.Mentions) {
                    Need(mention);
                }
            }
        }

        foreach (var d in byName) {
            var prints = selected[d];
            used += prints.Count;
            parts.Add($"{units[d].Name}:modules={string.Join(",", facts[d].Modules.Order(StringComparer.Ordinal))}:"
                + string.Join(",", prints.Select(static p => $"{p.Name}={p.Fingerprint}").Order(StringComparer.Ordinal)));
        }

        return string.Join("\n", parts);
    }

    /// <summary>What lowering one unit came to: its IR and its record of the types it laid out, or why not.</summary>
    /// <param name="Alone">The unit reported, alone, what the whole program's compilation may not: compile the program whole.</param>
    sealed record LoweredUnit(string? Ir, string Abi, IReadOnlyList<string> Errors, bool Alone = false) {
        /// <summary>A library unit's SIL as lowering made it, before the passes: what its interface keeps.</summary>
        public string? Sil { get; init; }

        /// <summary>What lowering said of the unit: among it, a library's generic code compiled once.</summary>
        public SilUnitInfo? Unit { get; init; }
    }

    /// <summary>
    ///     What a kept library unit's archive holds of its generic code
    ///     compiled once, as its interface records it; null when it has no
    ///     interface this compiler reads, or none of its generics are compiled
    ///     once — a unit lowered against it then lowers what it uses of them,
    ///     as it would of any other unit's.
    /// </summary>
    static LibraryInterface? CompiledInterfaceOf(Unit unit) {
        if (unit.Library is not { } library || !File.Exists(library.Interface)) {
            return null;
        }

        var face = LibraryInterface.Read(File.ReadAllBytes(library.Interface), CompilerIdentity.Current);
        if (face is not { Generics.Count: > 0 }) {
            return null;
        }

        var names = unit.Trees.Select(static t => t.Text.FileName).ToHashSet(StringComparer.Ordinal);
        return names.SetEquals(face.Sources.Select(static s => s.File)) ? face : null;
    }

    /// <summary>
    ///     What a program's declarations of the standard library are taken from
    ///     (<see cref="LibrarySymbols" />), when its unit is kept: the symbols
    ///     its entry has. Null when it is compiled now, or has none.
    /// </summary>
    static LibrarySymbols? SymbolsOf(Unit standard) =>
        LibrarySymbols.Mode != LibrarySymbolsMode.Source && standard is { Cached: true, Library.Symbols: var path } && File.Exists(path)
            ? LibrarySymbols.Read(File.ReadAllBytes(path), standard.Trees)
            : null;

    /// <summary>
    ///     What writes a library unit's interface in its entry, given the path
    ///     of its <c>.sbi</c> there; with the standard library's, its symbols.
    ///     Worked out here, before the unit's object is compiled on another thread.
    /// </summary>
    /// <param name="library">For the standard library's unit, the modules that come with the core.</param>
    static Action<string>? InterfaceFiles(LibraryUnit? unit, LibraryInterface? built, IReadOnlyList<SyntaxTree>? library, ParseOptions options) {
        if (unit is null || built is null) {
            return null;
        }

        var face = built.Write();
        var symbols = library is null ? null : LibrarySymbols.Of(library, options)?.Write();
        return path => {
            File.WriteAllBytes(path, face);
            if (symbols is not null) {
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(unit.Symbols)), symbols);
            }
        };
    }

    /// <summary>
    ///     The standard library unit's interface (<c>.sbi</c>, #182), as the
    ///     bytes its cache entry keeps beside the archive: worked out here,
    ///     before the unit's object is compiled on another thread, since
    ///     writing the symbols may bind what is not bound yet.
    /// </summary>
    static LibraryInterface? InterfaceOf(Compilation compilation, string name, IReadOnlyList<SyntaxTree> trees, LoweredUnit lowered, BuildSettings settings, ParseOptions options) {
        if (lowered.Sil is not { } sil) {
            return null;
        }

        var owned = trees.ToHashSet();
        return LibraryInterface.Of(compilation, name, owned.Contains, CompilerIdentity.Current, LibraryInterface.SettingsOf(options, settings.DenyReflection), sil, lowered.Unit);
    }

    /// <summary>
    ///     Lowers one unit of <paramref name="compilation" /> — the trees it
    ///     owns — verifies it, runs the SIL passes the settings ask for, and
    ///     writes it as LLVM IR.
    /// </summary>
    /// <param name="library">The standard library's unit, whose debug information is relative to no program's directory.</param>
    /// <param name="leads">The unit leads a library C links: it sets up the globals as the library loads.</param>
    /// <param name="libraries">The library units lowered or kept before this one, whose generic code compiled once it calls in their archives.</param>
    static LoweredUnit Lower(
        Compilation compilation, SilProgram index, IReadOnlyList<SyntaxTree> trees, bool library, bool leads, BuildSettings settings,
        IReadOnlyDictionary<string, string> embedded, BuildTimings? timings, IReadOnlyList<LibraryInterface>? libraries = null
    ) {
        var owned = trees.ToHashSet();
        SilModule module;
        IReadOnlyList<Diagnostic> lowering;
        string? sil;
        using (BuildTimings.Of(timings, "sil")) {
            var unit = new SilUnit(owned.Contains) {
                CompilesGenerics = library,
                Libraries = [..(libraries ?? []).Select(static l => l.CompiledGenerics())],
            };
            using var optimised = SilGen.UseOptimised(settings.OptimisedProgram);
            (module, lowering) = SilGen.LowerUnit(compilation, unit, index, denyReflection: settings.DenyReflection, library: leads);
            // A library's interface keeps the SIL other units may copy.
            sil = library && lowering.Count == 0 ? LibraryInterface.InlinableSil(module, module.Unit!) : null;

            // An optimised build copies in the libraries' inlinable generic
            // code it calls, for the specialisation pass to make its copies of.
            if (settings.SilLevel != "0" && lowering.Count == 0 && Specialise.Enabled) {
                foreach (var face in libraries ?? []) {
                    face.LinkInlinable(module);
                }
            }
            module.CheckExclusivity = settings.Exclusivity;
            module.CheckArc = settings.ArcChecks;
            if (!settings.OverflowChecks) {
                WrappingArithmetic.Apply(module);
            }
        }

        if (lowering.Count > 0) {
            return new(null, "", [..lowering.Select(static d => $"error[{d.Id}]: {d.Message}")], Alone: true);
        }

        var problems = timings is null ? SilVerifier.Verify(module) : timings.Measure("verify", () => SilVerifier.Verify(module));
        if (problems.Count > 0) {
            return new(null, "", [..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
        }

        // The opt-level's SIL passes run on the unit as on a whole program
        // (all but an unoptimised build with debug information, which keeps
        // every local in its slot). What they learn about a function from
        // its callees — whether a parameter escapes, whether a field is
        // written — they assume the worst of for a function another unit
        // defines, which this one only declares; the inliner copies in only
        // what the unit has the body of (its own functions, and the
        // instances of generic ones it uses), and devirtualisation leaves a
        // unit alone. Across units, ThinLTO inlines as the objects link.
        if (RunsPasses(settings)) {
            try {
                using var _ = BuildTimings.Of(timings, "passes");
                SilPassManager.Run(module, SilPassManager.ForLevel(settings.SilLevel));
            } catch (SilPassException failure) {
                return new(null, "", [$"internal error: {failure.Message}"]);
            }
        }

        // A library's files are named as the compiler embeds them, with
        // their text in the debug information, the same for every program:
        // its debug information is relative to no program's directory.
        string ir;
        using (BuildTimings.Of(timings, "emit")) {
            ir = settings.DebugDirectory is { } directory
                ? LlvmEmitter.EmitWithDebugInfo(module, library ? LibraryUnit.DebugDirectory : directory, settings.Native.TargetOrHost, settings.Optimise, embedded)
                : LlvmEmitter.Emit(module, settings.Native.TargetOrHost);
        }

        return new(ir, string.Join("\n", module.Unit!.Abi.Select(static p => $"{p.Key}\t{p.Value}")), []) { Sil = sil, Unit = module.Unit };
    }

    /// <summary>Whether a unit's SIL passes run: as a whole program's do, in every build but an unoptimised one with debug information.</summary>
    static bool RunsPasses(BuildSettings settings) => settings.DebugDirectory is null || settings.SilLevel != "0";

    /// <summary>
    ///     The fingerprint of the program's own units' extensions — those
    ///     that have any — which may change how the standard library's code
    ///     is named (docs/05 §5.4); "none" for a program that has none, so
    ///     that every such program links the same archive.
    /// </summary>
    static string OwnExtensions(IEnumerable<FileInterface[]> faces) {
        var none = Hash("");
        var extensions = faces.SelectMany(static f => f).Select(static f => f.Extensions).Where(e => e != none).ToList();
        return extensions.Count == 0 ? "none" : Hash(string.Join("\n", extensions));
    }

    /// <summary>
    ///     The build cache's key for the standard library's unit — Sable.Core
    ///     and the modules the program imports, parsed for its target and
    ///     profile: the files' names and text, the compiler, the target, the
    ///     settings that change code, and the program's extensions.
    /// </summary>
    static CacheKey StandardLibraryKey(BuildCache cache, BuildSettings settings, IReadOnlyList<SyntaxTree> trees, string extensions) {
        var source = CacheKey.Checksum(string.Join("\n", trees.Select(static t => t.Text.FileName).Order(StringComparer.Ordinal)
            .Zip(trees.OrderBy(static t => t.Text.FileName, StringComparer.Ordinal).Select(static t => CacheKey.Checksum(t.Text.Text)), static (name, text) => $"{name}={text}")));
        var modules = trees.Select(CoreLibrary.ModuleOf).OfType<string>().Distinct().Order(StringComparer.Ordinal);
        return cache.Key("library", "sable-std", CompilerIdentity.Version, source, settings.Native.TargetOrHost.Triple, [
            ..settings.CodeOptions,
            $"modules={string.Join(",", modules)}",
            $"extensions={extensions}",
        ]);
    }

    /// <summary>
    ///     <c>sable cache warm</c>: puts the standard library a program
    ///     importing <paramref name="imports" /> links — and the runtime — in
    ///     <paramref name="cache" /> for the settings' target and profile, as a
    ///     program without extensions of its own would find it.
    /// </summary>
    public static BuildOutcome Warm(IReadOnlyList<string> imports, BuildSettings settings, BuildCache cache) {
        var target = settings.Native.TargetOrHost;
        var options = Pipeline.ParseOptionsFor(!settings.DebugAssertions, ParseOptions.Default.ForTarget(target.Os, target.Arch));
        var program = SyntaxTree.Parse(new SourceText(string.Concat(imports.Select(static m => $"import {m}\n")), "warm.sb"), options);
        var library = Library.For([program]);
        // As a program's build has it (Separately): the program, with the
        // library's modules, and the core every compilation has.
        List<SyntaxTree> trees = [..library, ..CoreLibrary.TreesFor(program.Options)];
        var unit = LibraryUnit.In(cache, StandardLibraryKey(cache, settings.ForLibrary, trees, "none"), settings.Profile, "std", target);
        var runtime = NativeBuild.Runtime(settings.Native, NativeBuild.LtoFlags(settings.Native, target.Platform, NativeBuild.LinksBitcodeFor(target)), cache, settings.Profile);
        if (!runtime.Result.Succeeded) {
            return BuildOutcome.Failed([NativeBuild.Explained(runtime.Result, settings.Native).Report]);
        }

        if (unit.Exists) {
            return new(true, [], "fresh", 0, 2);
        }

        var compilation = Compilation.Create([program, ..library]);
        var errors = compilation.GetAllDiagnosticsByFile().Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error).Select(static d => $"error[{d.Diagnostic.Id}]: {d.Diagnostic.Message}").ToList();
        if (errors.Count > 0) {
            return BuildOutcome.Failed(errors);
        }

        var lowered = Lower(compilation, SilProgram.Index(compilation), trees, library: true, leads: false, settings.ForLibrary, Pipeline.EmbeddedSources(trees, [program]), null);
        if (lowered.Errors.Count > 0) {
            return BuildOutcome.Failed(lowered.Errors);
        }

        var face = InterfaceFiles(unit, InterfaceOf(compilation, "std", trees, lowered, settings, program.Options), library, program.Options);
        var written = unit.Write(lowered.Abi, temporary => NativeObjects.CompileIr(lowered.Ir!, temporary, settings.ForLibrary.Native), target, face);
        if (!written.Succeeded) {
            return BuildOutcome.Failed([$"internal error: compiling the standard library failed:\n{written.Output}"]);
        }

        Library.MarkChecked(trees, program.Options, cache.Root);
        return new(true, [], "warm", 1, 1);
    }

    /// <summary>
    ///     Keeps the object cache from growing without end: each build marks
    ///     the objects it used (through their <c>.abi</c> files — an object's
    ///     own time is in the executable's debug map, and must not change),
    ///     and past a limit the least recently used go. A library's archive is
    ///     the build cache's, marked used there and pruned by <c>sable cache</c>.
    /// </summary>
    static void Collect(string objects, IReadOnlyList<Unit> used) {
        var now = DateTime.UtcNow;
        foreach (var unit in used) {
            if (unit.Library is { } library) {
                BuildCache.MarkUsed(library.Directory);
            } else {
                File.SetLastWriteTimeUtc(unit.AbiPath, now);
            }
        }

        var limit = Math.Max(256, 8 * used.Count);
        var markers = new DirectoryInfo(objects).GetFiles("*.abi");
        if (markers.Length <= limit) {
            return;
        }

        foreach (var marker in markers.OrderBy(static m => m.LastWriteTimeUtc).Take(markers.Length - limit)) {
            File.Delete(Path.ChangeExtension(marker.FullName, ".o"));
            marker.Delete();
        }
    }

    /// <summary>The same for the <c>.sbi</c> files: those this build used are marked, and past a limit the least recently used go.</summary>
    static void CollectInterfaces(string interfaces, IReadOnlyList<string> used) {
        if (!Directory.Exists(interfaces)) {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var path in used.Where(File.Exists)) {
            File.SetLastWriteTimeUtc(path, now);
        }

        var limit = Math.Max(256, 8 * used.Count);
        var files = new DirectoryInfo(interfaces).GetFiles("*.sbi");
        foreach (var file in files.OrderBy(static f => f.LastWriteTimeUtc).Take(Math.Max(0, files.Length - limit))) {
            file.Delete();
        }
    }

    /// <summary>A type two units laid out differently, or null when they all agree.</summary>
    static string? Disagreement(IReadOnlyList<Unit> units) {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in units) {
            foreach (var line in File.ReadAllLines(unit.AbiPath)) {
                var tab = line.IndexOf('\t');
                if (tab < 0) {
                    continue;
                }

                var (type, shape) = (line[..tab], line[(tab + 1)..]);
                if (seen.TryGetValue(type, out var other) && other != shape) {
                    return type;
                }

                seen[type] = shape;
            }
        }

        return null;
    }

    /// <summary>
    ///     The errors of the files being compiled, and the program's
    ///     declarations', each formatted against its file, in program order —
    ///     or, asked for warnings, those of the files being compiled alone.
    /// </summary>
    static List<string> Diagnostics(Compilation compilation, IReadOnlyList<SyntaxTree> program, HashSet<SyntaxTree> checking, DiagnosticSeverity severity) {
        var order = program.Select(static (tree, index) => (tree, index)).ToDictionary(static p => p.tree, static p => p.index);
        var all = new List<(Diagnostic Diagnostic, SyntaxTree? Tree)>();
        var parse = new HashSet<Diagnostic>(ReferenceEqualityComparer.Instance);
        foreach (var tree in compilation.SyntaxTrees.Where(t => (severity == DiagnosticSeverity.Error || checking.Contains(t)) && !compilation.IsFromInterface(t))) {
            all.AddRange(tree.Diagnostics.Select(d => (d, (SyntaxTree?)tree)));
            parse.UnionWith(tree.Diagnostics);
        }

        foreach (var tree in compilation.SyntaxTrees.Where(checking.Contains)) {
            all.AddRange(compilation.GetSemanticModel(tree).GetDiagnostics().Select(d => (d, (SyntaxTree?)tree)));
        }

        // Declarations and signatures: the whole program's.
        all.AddRange(compilation.GetDiagnostics().Where(d => !parse.Contains(d)).Select(static d => (d, (SyntaxTree?)null)));
        return [
            ..all.Where(d => d.Diagnostic.Severity == severity && (severity == DiagnosticSeverity.Error || d.Tree is not null))
                .OrderBy(d => d.Tree is { } t && order.TryGetValue(t, out var index) ? index : int.MaxValue)
                .ThenBy(static d => d.Diagnostic.Span.Start)
                .Select(static d => d.Tree is { } file ? d.Diagnostic.Format(file.Text) : $"error[{d.Diagnostic.Id}]: {d.Diagnostic.Message}"),
        ];
    }

    /// <summary>
    ///     The line of a tool's output that says what went wrong: its first
    ///     error — a linker may warn first — or failing one its first line.
    /// </summary>
    static string FirstError(string text) {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(static l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? "";
    }

    static string Sanitise(string name) => string.Concat(name.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static string HashFile(string path) {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
