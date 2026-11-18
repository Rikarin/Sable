namespace Sable.Driver.Commands;

/// <summary><c>sable bindgen &lt;header.h&gt;</c>: Sable declarations for a C header (<see cref="Bindgen" />).</summary>
public sealed class BindgenCommand : Command {
    public override string Name => "bindgen";
    public override string Summary => "generate Sable declarations (@extern functions, structs, constants) from a C header";

    public override string Usage => """
        usage: sable bindgen <header.h> [-o <file.sb>] [--module <name>] [-- <clang arguments>]

          -o <file.sb>       write the declarations there (default: standard output)
          --module <name>    the module they are in (default: from the header's name: my_lib.h is MyLib)
          what follows '--' is passed to clang as it reads the header: -I <dir>, -D <name>=<value>

        Functions that take or return pointers, arrays or unions, and variadic ones, are listed
        in a comment and left out until Sable.Interop has pointers and C strings.
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        string? header = null;
        string? output = null;
        string? module = null;
        var clang = new List<string>();
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--":
                    clang.AddRange(arguments.Skip(i + 1));
                    i = arguments.Count;
                    break;
                case "-o" when i + 1 < arguments.Count: output = arguments[++i]; break;
                case "--module" when i + 1 < arguments.Count: module = arguments[++i]; break;
                default:
                    if (header is not null || arguments[i].StartsWith('-')) {
                        return BadUsage();
                    }

                    header = arguments[i];
                    break;
            }
        }

        if (header is null) {
            return BadUsage();
        }

        string text;
        try {
            text = Bindgen.Generate(header, module ?? Bindgen.ModuleNameFor(header), clang);
        } catch (BindgenException e) {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }

        if (output is null) {
            Console.Write(text);
        } else {
            File.WriteAllText(output, text);
            Console.Error.WriteLine($"       Wrote {output}");
        }

        return 0;
    }
}
