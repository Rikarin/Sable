using System.Text;
using System.Text.RegularExpressions;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable completions &lt;bash|zsh|fish&gt;</c>: a script that completes
///     <c>sable</c>'s commands and each command's options in that shell. It is
///     made from the commands themselves — their names and the options their
///     usage lists — so it cannot fall behind them.
/// </summary>
public sealed partial class CompletionsCommand : Command {
    public override string Name => "completions";
    public override string Summary => "print a shell completion script (bash, zsh or fish)";

    public override string Usage => """
        usage: sable completions <bash|zsh|fish>

          bash   eval "$(sable completions bash)" in ~/.bashrc
          zsh    sable completions zsh > "${fpath[1]}/_sable"
          fish   sable completions fish > ~/.config/fish/completions/sable.fish
        """;

    static readonly string[] GlobalFlags = ["-q", "--quiet", "-v", "--verbose", "--color", "--message-format", "-j", "--jobs", "--offline", "-V", "--version", "-h", "--help"];

    [GeneratedRegex(@"(?<![\w-])(--?[A-Za-z][\w-]*)")]
    private static partial Regex Option();

    /// <summary>The options a command's usage names, in the order it names them.</summary>
    public static IReadOnlyList<string> OptionsOf(Command command) =>
        [..Option().Matches(command.Usage).Select(static m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments is not [var shell]) {
            return BadUsage();
        }

        string? script = shell switch {
            "bash" => Bash(),
            "zsh" => Zsh(),
            "fish" => Fish(),
            _ => null,
        };
        if (script is null) {
            return BadUsage();
        }

        Console.Write(script);
        return 0;
    }

    static string Bash() {
        var builder = new StringBuilder();
        builder.Append("# sable completions for bash: eval \"$(sable completions bash)\"\n");
        builder.Append("_sable() {\n");
        builder.Append("    local cur=\"${COMP_WORDS[COMP_CWORD]}\" command=\"\" i\n");
        builder.Append("    for ((i = 1; i < COMP_CWORD; i++)); do\n");
        builder.Append("        case \"${COMP_WORDS[i]}\" in -*) ;; *) command=\"${COMP_WORDS[i]}\"; break ;; esac\n");
        builder.Append("    done\n");
        builder.Append("    local words\n");
        builder.Append("    case \"$command\" in\n");
        builder.Append($"        \"\") words=\"{string.Join(' ', Command.All.Select(static c => c.Name))} help {string.Join(' ', GlobalFlags)}\" ;;\n");
        foreach (var command in Command.All) {
            builder.Append($"        {command.Name}) words=\"{string.Join(' ', OptionsOf(command).Concat(GlobalFlags).Distinct(StringComparer.Ordinal))}\" ;;\n");
        }

        builder.Append("        help) words=\"" + string.Join(' ', Command.All.Select(static c => c.Name)) + "\" ;;\n");
        builder.Append("    esac\n");
        builder.Append("    COMPREPLY=($(compgen -W \"$words\" -- \"$cur\"))\n");
        builder.Append("    case \"$cur\" in -*) ;; *) [[ -n \"$command\" ]] && COMPREPLY+=($(compgen -f -- \"$cur\")) ;; esac\n");
        builder.Append("}\n");
        builder.Append("complete -F _sable sable\n");
        return builder.ToString();
    }

    static string Zsh() {
        static string Escape(string text) => text.Replace("'", "'\\''", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
        var builder = new StringBuilder();
        builder.Append("#compdef sable\n");
        builder.Append("# sable completions for zsh: sable completions zsh > \"${fpath[1]}/_sable\"\n");
        builder.Append("_sable() {\n");
        builder.Append("    local -a commands\n");
        builder.Append("    commands=(\n");
        foreach (var command in Command.All) {
            builder.Append($"        '{command.Name}:{Escape(command.Summary)}'\n");
        }

        builder.Append("        'help:show a command'\"'\"'s arguments'\n");
        builder.Append("    )\n");
        builder.Append("    if (( CURRENT == 2 )); then\n");
        builder.Append($"        _describe 'command' commands; compadd -- {string.Join(' ', GlobalFlags)}\n");
        builder.Append("        return\n");
        builder.Append("    fi\n");
        builder.Append("    case \"${words[2]}\" in\n");
        foreach (var command in Command.All) {
            builder.Append($"        {command.Name}) compadd -- {string.Join(' ', OptionsOf(command).Concat(GlobalFlags).Distinct(StringComparer.Ordinal))}; _files ;;\n");
        }

        builder.Append("        help) _describe 'command' commands ;;\n");
        builder.Append("    esac\n");
        builder.Append("}\n");
        builder.Append("_sable \"$@\"\n");
        return builder.ToString();
    }

    static string Fish() {
        static string Escape(string text) => text.Replace("'", "\\'", StringComparison.Ordinal);
        var builder = new StringBuilder();
        builder.Append("# sable completions for fish: sable completions fish > ~/.config/fish/completions/sable.fish\n");
        builder.Append("complete -c sable -f\n");
        foreach (var command in Command.All) {
            builder.Append($"complete -c sable -n '__fish_use_subcommand' -a '{command.Name}' -d '{Escape(command.Summary)}'\n");
        }

        builder.Append($"complete -c sable -n '__fish_seen_subcommand_from help' -a '{string.Join(' ', Command.All.Select(static c => c.Name))}'\n");
        foreach (var command in Command.All) {
            foreach (var option in OptionsOf(command)) {
                // fish's -l is a long option, -s a one-letter one, -o one dash and several letters (-O0).
                var flag = option.StartsWith("--", StringComparison.Ordinal) ? $"-l '{option[2..]}'"
                    : option.Length == 2 ? $"-s '{option[1..]}'"
                    : $"-o '{option[1..]}'";

                builder.Append($"complete -c sable -n '__fish_seen_subcommand_from {command.Name}' {flag} -F\n");
            }
        }

        foreach (var option in GlobalFlags.Where(static f => f.StartsWith("--", StringComparison.Ordinal))) {
            builder.Append($"complete -c sable -l '{option[2..]}'\n");
        }

        return builder.ToString();
    }
}
