using Sable.Syntax;
using Sable.Visualiser;

// sable-visualise <file.sab> [--html <out.html>] [--trivia] [--syntax-only]
//
// The syntax-tree visualiser of docs/04 §0.5. With no options it prints the
// tree as indented text; with --html it writes a page that shows the source
// beside the tree, where selecting either highlights the other.

string? input = null;
string? html = null;
var trivia = false;
var semantics = true;

for (var i = 0; i < args.Length; i++) {
    switch (args[i]) {
        case "--html" when i + 1 < args.Length: html = args[++i]; break;
        case "--trivia": trivia = true; break;
        case "--syntax-only": semantics = false; break;
        case "-h" or "--help": return Usage(0);
        default:
            if (input is not null || args[i].StartsWith('-')) {
                return Usage(2);
            }

            input = args[i];
            break;
    }
}

if (input is null) {
    return Usage(2);
}

if (!File.Exists(input)) {
    Console.Error.WriteLine($"sable-visualise: no such file '{input}'");
    return 1;
}

var source = new SourceText(File.ReadAllText(input), Path.GetFileName(input));
var document = VisualTree.Build(source, semantics);

if (html is null) {
    Console.Write(TextRenderer.Render(document, trivia));
} else {
    File.WriteAllText(html, HtmlRenderer.Render(document));
    Console.WriteLine($"wrote {html}");
}

return 0;

static int Usage(int code) {
    var writer = code == 0 ? Console.Out : Console.Error;
    writer.WriteLine("usage: sable-visualise <file.sab> [--html <out.html>] [--trivia] [--syntax-only]");
    writer.WriteLine();
    writer.WriteLine("  --html <out.html>  write an interactive page instead of printing text");
    writer.WriteLine("  --trivia           include whitespace, newlines and comments in the text dump");
    writer.WriteLine("  --syntax-only      skip binding: no types, symbols or semantic diagnostics");
    return code;
}
