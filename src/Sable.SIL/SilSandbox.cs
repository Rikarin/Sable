namespace Sable.SIL;

/// <summary>What a program run by the interpreter asks of the machine: to read a path, write one, or run a program.</summary>
public enum SilAccess {
    /// <summary>Read a file, list a directory, or ask whether a path exists.</summary>
    Read,

    /// <summary>Write, remove or make a file or directory.</summary>
    Write,
}

/// <summary>
///     What an interpreted program may do outside itself, when it is not to
///     do everything (<see cref="SilInterpreter.Sandbox" />): a build script's
///     declared powers (docs/05 §5.6). Every runtime call that touches the
///     file system, the environment or another process asks it first; one it
///     refuses stops the program with a <see cref="SilTrap" /> naming what was
///     asked, rather than failing quietly as a missing file would.
/// </summary>
public abstract class SilSandbox {
    /// <summary>The program's working directory, which relative paths are taken from. It is the sandbox's, not the process's.</summary>
    public abstract string CurrentDirectory { get; set; }

    /// <summary>The full path to use for <paramref name="path" />; traps when the program may not have that access to it.</summary>
    public abstract string Check(string path, SilAccess access);

    /// <summary>The program to start for <paramref name="program" />; traps when the program may not run it.</summary>
    public abstract string CheckProgram(string program);

    /// <summary>An environment variable as the program sees it; null when it has none of that name.</summary>
    public abstract string? Environment(string name);

    /// <summary>The names of the variables the program may see, for a list of them all; none by default.</summary>
    public virtual IEnumerable<string> EnvironmentNames => [];

    /// <summary>The environment a program it runs is started with, over the process's own.</summary>
    public abstract IReadOnlyDictionary<string, string> ProgramEnvironment { get; }
}
