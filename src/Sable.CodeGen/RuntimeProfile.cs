using System.Text;
using System.Text.RegularExpressions;

namespace Sable.CodeGen;

/// <summary>
///     Which parts of the runtime a program links (docs/06 §6.1):
///     <c>runtime = "minimal"</c> in a manifest's <c>[targets.&lt;triple&gt;]</c>,
///     or <c>--runtime minimal</c>. Each includes the one before it. The
///     runtime's source decides what each holds (<c>runtime/sable_runtime.c</c>,
///     <c>SABLE_RUNTIME_PROFILE</c>).
/// </summary>
public enum RuntimeProfile {
    /// <summary>
    ///     The core: objects, memory, references, traps, numbers, strings,
    ///     hashing, arrays, metadata, inline layout, the leak check and the
    ///     standard streams. One thread and no task.
    /// </summary>
    Minimal = 1,

    /// <summary>
    ///     The core, the executor and threads, the system (files, sockets,
    ///     processes, the terminal, IPC, trust) and the processor's kernels.
    /// </summary>
    Standard = 2,

    /// <summary>Everything: the standard runtime and the tools — backtraces named by line, coverage, call profiles.</summary>
    Full = 3,
}

/// <summary>What is said of a <see cref="RuntimeProfile" />, and the stubs of what it leaves out.</summary>
public static partial class RuntimeProfiles {
    /// <summary>The profile's name as a manifest and <c>--runtime</c> write it.</summary>
    public static string Name(this RuntimeProfile profile) => profile switch {
        RuntimeProfile.Minimal => "minimal",
        RuntimeProfile.Standard => "standard",
        _ => "full",
    };

    /// <summary>The profile <paramref name="name" /> names; null for any other text.</summary>
    public static RuntimeProfile? Parse(string name) => name switch {
        "minimal" => RuntimeProfile.Minimal,
        "standard" => RuntimeProfile.Standard,
        "full" => RuntimeProfile.Full,
        _ => null,
    };

    /// <summary>What a manifest or the command line is told when a profile's name is none of them.</summary>
    public const string Expected = "one of \"minimal\", \"standard\" and \"full\"";

    /// <summary>
    ///     The C compiler's flag that picks the profile's parts; none for the
    ///     full runtime, which is compiled as it always was.
    /// </summary>
    public static IReadOnlyList<string> Flags(this RuntimeProfile profile) =>
        profile == RuntimeProfile.Full ? [] : [$"-DSABLE_RUNTIME_PROFILE={(int)profile}"];

    /// <summary>
    ///     LLVM IR defining a stub for each function the full runtime
    ///     exports and the profile's does not — each traps, naming itself,
    ///     through <c>sable_runtime_absent</c> — and a zeroed variable for each
    ///     such variable: what a program, and the standard library compiled
    ///     whole into an archive, may refer to without reaching it, so that
    ///     every link of a smaller runtime succeeds wherever the full one's
    ///     does. Both are the runtime compiled to IR for the same target.
    /// </summary>
    public static string AbsentStubs(string fullIr, string profileIr) {
        var defined = Exported(profileIr).Select(static e => e.Name).ToHashSet(StringComparer.Ordinal);
        var stubs = new StringBuilder();
        foreach (var line in fullIr.Split('\n')) {
            // The module's own: its target, and the types a signature names.
            if (line.StartsWith("target ", StringComparison.Ordinal) || TypeDefinition().IsMatch(line)) {
                stubs.Append(line).Append('\n');
            }
        }

        stubs.Append("declare void @sable_runtime_absent(ptr) noreturn\n");
        var index = 0;
        foreach (var (name, line, function) in Exported(fullIr)) {
            if (defined.Contains(name)) {
                continue;
            }

            if (function) {
                var header = AttributeGroup().Replace(line, "").TrimEnd();
                var text = Encoding.UTF8.GetBytes(name);
                stubs.Append($"@.absent.{index} = private unnamed_addr constant [{text.Length + 1} x i8] c\"{name}\\00\"\n");
                stubs.Append(header).Append('\n');
                stubs.Append($"  call void @sable_runtime_absent(ptr @.absent.{index})\n  unreachable\n}}\n");
                index++;
            } else {
                stubs.Append(ZeroedVariable(line)).Append('\n');
            }
        }

        return stubs.ToString();
    }

    /// <summary>Each function and variable a module of IR defines that another can link to: its name, its line, and whether it is a function.</summary>
    static IEnumerable<(string Name, string Line, bool Function)> Exported(string ir) {
        foreach (var raw in ir.Split('\n')) {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("define ", StringComparison.Ordinal)) {
                if (!Local(line[..line.IndexOf('@')]) && FunctionName().Match(line) is { Success: true } match) {
                    yield return (match.Groups[1].Value, line, true);
                }
            } else if (VariableDefinition().Match(line) is { Success: true } variable && !Local(variable.Groups[2].Value)) {
                yield return (variable.Groups[1].Value, line, false);
            }
        }
    }

    /// <summary>Whether linkage words say a definition is the module's own, or someone else's.</summary>
    static bool Local(string words) =>
        words.Split(' ').Any(static w => w is "internal" or "private" or "available_externally" or "external" or "extern_weak" or "linkonce_odr" or "linkonce");

    /// <summary>A variable's definition, its value zero: what the runtime's parts are given of one whose part is left out.</summary>
    static string ZeroedVariable(string line) {
        var keyword = VariableKeyword().Match(line);
        var start = keyword.Index + keyword.Length;
        var end = TypeEnd(line, start);
        var align = Alignment().Match(line);
        return line[..end] + " zeroinitializer" + (align.Success ? align.Value : "");
    }

    /// <summary>Where the type that starts at <paramref name="start" /> ends: brackets balanced, or the first space.</summary>
    static int TypeEnd(string line, int start) {
        var depth = 0;
        for (var i = start; i < line.Length; i++) {
            switch (line[i]) {
                case '{' or '[' or '<' or '(':
                    depth++;
                    break;
                case '}' or ']' or '>' or ')':
                    depth--;
                    break;
                case ' ' when depth == 0:
                    return i;
            }
        }

        return line.Length;
    }

    [GeneratedRegex(@"^%[\w.$""]+ = type ")]
    private static partial Regex TypeDefinition();

    [GeneratedRegex(@"@([A-Za-z_$][\w.$]*)\(")]
    private static partial Regex FunctionName();

    [GeneratedRegex(@"^@([A-Za-z_$][\w.$]*) = ([^""=]*?)\b(global|constant) ")]
    private static partial Regex VariableDefinition();

    [GeneratedRegex(@"\b(global|constant) ")]
    private static partial Regex VariableKeyword();

    [GeneratedRegex(@", align \d+$")]
    private static partial Regex Alignment();

    [GeneratedRegex(@" #\d+")]
    private static partial Regex AttributeGroup();
}
