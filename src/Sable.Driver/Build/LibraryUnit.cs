using Sable.CodeGen;

namespace Sable.Driver.Build;

/// <summary>
///     A library's unit compiled (docs/05 §5.4): an entry of the
///     <see cref="BuildCache" /> holding its object, the archive of it a
///     program links against, and the record of the types it laid out
///     (<c>.abi</c>), which the units linked with it must agree with. Written
///     whole or not at all, so that builds running at once — of any project —
///     share it.
/// </summary>
/// <remarks>
///     A program links the archive, not the object: a linker takes from an
///     archive what the program needs of it. A static library C links takes
///     the object, whole.
/// </remarks>
public sealed class LibraryUnit {
    /// <summary>
    ///     What a library's debug information is relative to: no program's
    ///     directory, since the same archive is linked into every program. Its
    ///     files are named as the compiler embeds them (<c>lib/Sable.Core/Array.sb</c>),
    ///     and their text is in the debug information.
    /// </summary>
    public const string DebugDirectory = ".";

    readonly BuildCache _cache;
    readonly CacheKey _key;
    readonly string _profile;

    LibraryUnit(BuildCache cache, CacheKey key, string profile, string name, NativeTarget target) {
        (_cache, _key, _profile) = (cache, key, profile);
        Directory = cache.DirectoryFor(key);
        Name = name;
        Object = Path.Combine(Directory, name + (target.Platform == NativePlatform.Windows ? ".obj" : ".o"));
        Archive = Path.Combine(Directory, NativeBuild.LibraryFileName("sable_" + name, LibraryKind.Static, target));
        Abi = Path.Combine(Directory, name + ".abi");
        Interface = Path.Combine(Directory, name + ".sbi");
    }

    /// <summary>The unit's entry; every file of it is there, or the directory is not.</summary>
    public string Directory { get; }

    public string Name { get; }

    public string Object { get; }

    public string Archive { get; }

    public string Abi { get; }

    /// <summary>
    ///     Where the unit's interface goes, beside the archive it describes:
    ///     the bound symbols and the SIL other units need (#182's <c>.sbi</c>,
    ///     stages 2–4; <see cref="Sable.SIL.LibraryInterface" />), written
    ///     through <see cref="Write" />'s <c>writeInterface</c> hook.
    /// </summary>
    public string Interface { get; }

    public bool Exists => System.IO.Directory.Exists(Directory);

    /// <summary>The unit kept in <paramref name="cache" /> under <paramref name="key" />, for <paramref name="target" />.</summary>
    /// <param name="profile">The profile the build is for, which the entry records.</param>
    public static LibraryUnit In(BuildCache cache, CacheKey key, string profile, string name, NativeTarget target) => new(cache, key, profile, name, target);

    /// <summary>
    ///     Writes the unit, unless another build has: its <paramref name="abi" />
    ///     record, its object as <paramref name="compile" /> makes it at the
    ///     path it is given, and the archive of that object.
    /// </summary>
    /// <param name="writeInterface">
    ///     The hook for the unit's interface: given the path it goes to in
    ///     the entry being made, it writes the <c>.sbi</c> there, which is then
    ///     published with the archive, whole.
    /// </param>
    public NativeBuildResult Write(string abi, Func<string, NativeBuildResult> compile, NativeTarget target, Action<string>? writeInterface = null) =>
        _cache.GetOrCreate(_key, _profile, temporary => {
            File.WriteAllText(Path.Combine(temporary, Path.GetFileName(Abi)), abi);
            var objectPath = Path.Combine(temporary, Path.GetFileName(Object));
            var compiled = compile(objectPath);
            if (!compiled.Succeeded) {
                return compiled;
            }

            writeInterface?.Invoke(Path.Combine(temporary, Path.GetFileName(Interface)));
            var archived = NativeBuild.Archive([objectPath], Path.Combine(temporary, Path.GetFileName(Archive)), target);
            return archived.Succeeded ? compiled : archived;
        }).Result;
}
