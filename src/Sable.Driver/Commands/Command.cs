namespace Sable.Driver.Commands;

/// <summary>
///     One of the <c>sable</c> binary's commands (docs/05 §5.1). Each lives in
///     a file of its own under <c>Commands/</c> and is found by reflection, so
///     adding a command touches nothing else.
/// </summary>
public abstract class Command {
    /// <summary>The word that selects it: <c>sable &lt;Name&gt; …</c>.</summary>
    public abstract string Name { get; }

    /// <summary>One line for <c>sable --help</c>.</summary>
    public abstract string Summary { get; }

    /// <summary>Its usage lines, printed by <c>sable &lt;Name&gt; --help</c> and on a bad argument.</summary>
    public abstract string Usage { get; }

    /// <summary>Runs it on the arguments after its name; returns the process's exit code.</summary>
    public abstract int Run(IReadOnlyList<string> arguments);

    /// <summary>Prints the usage and returns 2, for a bad argument.</summary>
    protected int BadUsage() {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    static readonly Lazy<IReadOnlyList<Command>> Discovered = new(static () => [
        ..typeof(Command).Assembly.GetTypes()
            .Where(static t => t is { IsAbstract: false, IsClass: true } && t.IsSubclassOf(typeof(Command)) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(static t => (Command)Activator.CreateInstance(t)!)
            .OrderBy(static c => c.Name, StringComparer.Ordinal),
    ]);

    /// <summary>Every command there is, by name.</summary>
    public static IReadOnlyList<Command> All => Discovered.Value;

    /// <summary>The command named <paramref name="name" />, if there is one.</summary>
    public static Command? Find(string name) => All.FirstOrDefault(c => c.Name == name);
}
