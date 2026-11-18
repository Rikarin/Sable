namespace Sable.Driver;

/// <summary>How much a command says about what it is doing.</summary>
public enum Verbosity {
    /// <summary><c>-q</c>: errors and warnings only, no "Compiling …" lines.</summary>
    Quiet,
    Normal,

    /// <summary><c>-v</c>: also the commands it runs (clang, the linker) and where it looks for packages.</summary>
    Verbose,
}

/// <summary>Whether diagnostics and status lines are coloured.</summary>
public enum ColorChoice {
    /// <summary>When standard error is a terminal and <c>NO_COLOR</c> is not set.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>How diagnostics are written: for a person, or one JSON object a line for a tool.</summary>
public enum MessageFormat {
    Human,
    Json,
}

/// <summary>
///     The options every command takes (docs/05 §5.1): <c>-q</c>, <c>-v</c>,
///     <c>--color</c>, <c>--message-format</c>, <c>-j</c> and <c>--offline</c>.
///     None of them changes what a build produces — only what it says, how
///     many things it does at once, and whether it may reach the registry —
///     which is the design rule they are held to.
/// </summary>
/// <remarks>
///     They are taken from anywhere before a <c>--</c>, as Cargo takes them, so
///     <c>sable -q build</c> and <c>sable build -q</c> mean the same. They are
///     process-wide because they are the process's: the driver is one command
///     run once.
/// </remarks>
public sealed record GlobalOptions {
    public Verbosity Verbosity { get; init; } = Verbosity.Normal;
    public ColorChoice Color { get; init; } = ColorChoice.Auto;
    public MessageFormat MessageFormat { get; init; } = MessageFormat.Human;

    /// <summary><c>-j N</c>: at most this many jobs (clang processes, parsing threads) at once; null for one a core.</summary>
    public int? Jobs { get; init; }

    /// <summary><c>--offline</c>: never reach the registry; resolve from the cache and <c>vendor/</c> only.</summary>
    public bool Offline { get; init; }

    /// <summary>The options of this process, as <see cref="Parse" /> found them.</summary>
    public static GlobalOptions Current { get; set; } = new();

    /// <summary>How many things to do at once: <c>-j</c>'s number, else one a core.</summary>
    public int Parallelism => Jobs ?? Environment.ProcessorCount;

    public ParallelOptions ParallelOptions => new() { MaxDegreeOfParallelism = Parallelism };

    /// <summary>
    ///     The global options in <paramref name="arguments" />, and the
    ///     arguments without them; null, with the reason, for one that is
    ///     malformed (<c>-j</c> without a number, an unknown colour).
    /// </summary>
    public static (GlobalOptions Options, string[] Arguments)? Parse(IReadOnlyList<string> arguments, out string? error) {
        var options = new GlobalOptions();
        var rest = new List<string>();
        error = null;
        for (var i = 0; i < arguments.Count; i++) {
            var argument = arguments[i];
            string? Value(string name) {
                if (argument.StartsWith(name + "=", StringComparison.Ordinal)) {
                    return argument[(name.Length + 1)..];
                }

                return i + 1 < arguments.Count ? arguments[++i] : null;
            }

            if (argument == "--") {
                // What follows is the program's, or the harness's.
                rest.AddRange(arguments.Skip(i));
                break;
            }

            switch (argument) {
                case "-q" or "--quiet":
                    options = options with { Verbosity = Verbosity.Quiet };
                    continue;
                case "-v" or "--verbose":
                    options = options with { Verbosity = Verbosity.Verbose };
                    continue;
                case "--offline":
                    options = options with { Offline = true };
                    continue;
            }

            if (argument == "--color" || argument.StartsWith("--color=", StringComparison.Ordinal)) {
                switch (Value("--color")) {
                    case "auto": options = options with { Color = ColorChoice.Auto }; continue;
                    case "always": options = options with { Color = ColorChoice.Always }; continue;
                    case "never": options = options with { Color = ColorChoice.Never }; continue;
                    case var other:
                        error = $"--color takes auto, always or never, not '{other}'";
                        return null;
                }
            }

            if (argument == "--message-format" || argument.StartsWith("--message-format=", StringComparison.Ordinal)) {
                switch (Value("--message-format")) {
                    case "human": options = options with { MessageFormat = MessageFormat.Human }; continue;
                    case "json": options = options with { MessageFormat = MessageFormat.Json }; continue;
                    case var other:
                        error = $"--message-format takes human or json, not '{other}'";
                        return null;
                }
            }

            if (argument is "-j" or "--jobs" || argument.StartsWith("--jobs=", StringComparison.Ordinal) || (argument.StartsWith("-j", StringComparison.Ordinal) && argument.Length > 2 && char.IsAsciiDigit(argument[2]))) {
                var text = argument.StartsWith("-j", StringComparison.Ordinal) && argument.Length > 2 ? argument[2..] : Value("--jobs");
                if (!int.TryParse(text, out var jobs) || jobs < 1) {
                    error = $"-j takes a number of jobs, at least 1, not '{text}'";
                    return null;
                }

                options = options with { Jobs = jobs };
                continue;
            }

            rest.Add(argument);
        }

        return (options, [..rest]);
    }

    /// <summary>Whether to colour what goes to standard error: asked for, or a terminal without <c>NO_COLOR</c> (https://no-color.org).</summary>
    public bool ColorsErrors => Color switch {
        ColorChoice.Always => true,
        ColorChoice.Never => false,
        _ => MessageFormat == MessageFormat.Human
             && Environment.GetEnvironmentVariable("NO_COLOR") is not { Length: > 0 }
             && Environment.GetEnvironmentVariable("TERM") != "dumb"
             && !Console.IsErrorRedirected,
    };

    /// <summary>The flags, for <c>sable --help</c>.</summary>
    public const string Help = """
        Options every command takes, before or after its name:

          -q, --quiet                 say only what went wrong: no "Compiling …" lines
          -v, --verbose               also print the commands run (clang, the linker)
          --color <auto|always|never> colour the output (default: auto, which NO_COLOR turns off)
          --message-format <human|json>
                                      json: each diagnostic as one JSON object a line, on standard output
          -j, --jobs <n>              run at most n jobs at once (default: one a core)
          --offline                   never reach the registry: use the cache and vendor/ only
          -V, --version               print the toolchain's version
        """;
}
