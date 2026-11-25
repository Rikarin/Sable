using Sable.Driver.Generators;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Source generators (docs/08): what a generator is given, what it writes
///     and where that goes, the rules it is held to, and the cache.
/// </summary>
public sealed class SourceGeneratorTests {
    /// <summary>A generator that gives each field of a struct a describing method.</summary>
    const string Describer = """
        module Describe

        import Sable.Generators
        import Sable.Text

        @generator("describe", members: ["hidden"])
        fn describe(_ declaration: Declaration, into output: inout Output) {
            let text = TextBuilder()
            let label = declaration.trigger.string(named: "label") ?? declaration.name
            text.appendLine("extension \(declaration.typeName) {")
            text.appendLine("    fn describe() -> String {")
            var parts: String[>..] = []
            for field in declaration.fields {
                if field.attribute(named: "hidden") == none {
                    parts.append("\(field.name): \\(self.\(field.name))")
                }
            }
            let all = parts.joined(separator: ", ")
            text.appendLine("        return \"\(label) { \(all) }\"")
            text.appendLine("    }")
            text.appendLine("}")
            output.add(file: "\(declaration.name).describe.sb", text.build())
        }
        """;

    static GeneratorSet Set(params (string Name, string Text)[] files) => GeneratorSet.FromSources("gen", files);

    static GenerationResult Run(GeneratorSet set, params string[] sources) =>
        SourceGenerators.Run("app", [set], [..sources.Select(static (s, i) => SyntaxTree.Parse(new SourceText(s, $"src/file{i}.sb")))]);

    static IReadOnlyList<string> Messages(GenerationResult result) =>
        [..result.Diagnostics.Select(static d => $"{d.Diagnostic.Id} {d.Diagnostic.Message}")];

    /// <summary>The program with what the generators wrote, interpreted: its output.</summary>
    static string RunProgram(GeneratorSet set, string source) {
        // As a package that can see the generators parses its files.
        var options = ParseOptions.Default with { GeneratorAttributes = [..set.Attributes.Types], GeneratorMemberAttributes = [..set.Attributes.Members] };
        var tree = SyntaxTree.Parse(new SourceText(source, "src/main.sb"), options);
        var result = SourceGenerators.Run("app", [set], [tree]);
        Assert.False(result.HasErrors, string.Join("\n", Messages(result)));
        var generated = result.Files.Select(f => SyntaxTree.Parse(new SourceText(f.Text, $"target/generated/app/{f.Generator}/{f.FileName}"), options));
        var compiled = Pipeline.Compile([tree, ..generated]);
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors) + "\n\n" + string.Join("\n\n", result.Files.Select(static f => f.Text)));
        var interpreter = new SilInterpreter(compiled.Sil!);
        interpreter.Run(compiled.Sil!.EntryPoint!);
        return interpreter.Output.ToString();
    }

    [Fact]
    public void AGeneratedExtensionIsCompiledWithTheProgram() =>
        Assert.Equal("Point { x: 1, y: 2 }\nlabelled { name: a }\n", RunProgram(Set(("generators/describe.sb", Describer)), """
            module App

            @describe
            struct Point {
                var x: Int
                let y: Int
            }

            @describe(label: "labelled")
            struct Named {
                var name: String
                @hidden var secret: Int = 7
            }

            fn main() {
                print(Point(x: 1, y: 2).describe())
                print(Named(name: "a").describe())
            }
            """));

    /// <summary>A generator that writes what it was given into the file it writes, as a comment.</summary>
    const string Echo = """
        module Echo

        import Sable.Generators
        import Sable.Text

        @generator("echo")
        fn echo(_ d: Declaration, into output: inout Output) {
            let t = TextBuilder()
            t.appendLine("// \(d.keyword) \(d.name) in \(d.moduleName) public=\(d.isPublic) from \(d.file)")
            t.appendLine("// parameters [\(d.typeParameters.joined(separator: ","))]")
            t.appendLine("// conformances [\(d.conformances.joined(separator: ","))]")
            t.appendLine("// imports \(d.imports.joined(separator: ","))")
            for a in d.attributes {
                var arguments: String[>..] = []
                for argument in a.arguments {
                    arguments.append("\(argument.label)=\(argument.text)/\(argument.string ?? "-")")
                }
                t.appendLine("// attribute \(a.name)(\(arguments.joined(separator: ";")))")
            }
            for f in d.fields {
                t.appendLine("// field \(f.name): \(f.type) var=\(f.isMutable) default=\(f.hasDefault) public=\(f.isPublic) attributes=\(f.attributes.count)")
            }
            for c in d.cases {
                var payload: String[>..] = []
                for p in c.payload {
                    payload.append("\(p.name):\(p.type)")
                }
                t.appendLine("// case \(c.name)(\(payload.joined(separator: ","))) raw=\(c.rawValue ?? "-") attributes=\(c.attributes.count)")
            }
            output.add(file: "\(d.name).echo.sb", t.build())
        }
        """;

    static string Comments(GeneratedFile file) =>
        string.Join("\n", file.Text.Split('\n').Where(static l => l.StartsWith("// ", StringComparison.Ordinal) && !l.StartsWith("// Written", StringComparison.Ordinal)));

    [Fact]
    public void AGeneratorIsGivenTheDeclarationAsWritten() {
        var result = Run(Set(("generators/echo.sb", Echo)), """
            module App

            import Sable.Text

            @echo(tag: "a\tb", 3)
            public struct Pair<A, B>: Equatable {
                public var first: A
                let second: B? = none
                static let count = 2
                var computed: Int => 1
                @rename("s") var third: String
            }

            @echo
            union Shape {
                @tag Circle(radius: F64),
                Point,
                Rect(F64, F64)
            }

            @echo
            enum Level {
                Low = 1,
                High
            }

            @other
            struct Ignored { }
            """);
        Assert.False(result.HasErrors, string.Join("\n", Messages(result)));
        Assert.Equal(3, result.Files.Count);
        Assert.Equal("""
            // struct Pair in App public=true from src/file0.sb
            // parameters [A,B]
            // conformances [Equatable]
            // imports Sable.Text
            // attribute echo(tag="a\tb"/a	b;=3/-)
            // field first: A var=true default=false public=true attributes=0
            // field second: B? var=false default=true public=false attributes=0
            // field third: String var=true default=false public=false attributes=1
            """, Comments(result.Files[0]));
        Assert.Equal("""
            // union Shape in App public=false from src/file0.sb
            // parameters []
            // conformances []
            // imports Sable.Text
            // attribute echo()
            // case Circle(radius:F64) raw=- attributes=1
            // case Point() raw=- attributes=0
            // case Rect(:F64,:F64) raw=- attributes=0
            """, Comments(result.Files[1]));
        Assert.Contains("// case Low() raw=1 attributes=0\n// case High() raw=- attributes=0", Comments(result.Files[2]));
    }

    [Fact]
    public void AGeneratedFileIsInTheDeclarationsModuleWithItsImports() {
        var result = Run(Set(("generators/echo.sb", Echo)), """
            module App.Model

            import Sable.Text
            import Sable.Collections

            @echo
            struct User { }
            """);
        var file = Assert.Single(result.Files);
        Assert.Equal("echo", file.Generator);
        Assert.Equal("User.echo.sb", file.FileName);
        Assert.StartsWith("// Written by the generator 'echo' for 'User' in src/file0.sb.", file.Text);
        Assert.Contains("\nmodule App.Model\nimport Sable.Text\nimport Sable.Collections\n\n// struct User", file.Text);
    }

    [Fact]
    public void AGeneratorsMessagesAreReportedAtTheAttribute() {
        var set = Set(("generators/check.sb", """
            module Check

            import Sable.Generators

            @generator("checked")
            fn check(_ d: Declaration, into output: inout Output) {
                if d.keyword != "struct" {
                    output.error("only a struct can be @checked, not a \(d.keyword)")
                }
                if d.fields.isEmpty {
                    output.warning("'\(d.name)' has no fields to check")
                }
            }
            """));
        var source = "module App\n\n@checked\nunion U { A }\n\n@checked\nstruct S { }\n";
        var result = Run(set, source);
        Assert.Equal([
            "S3232 checked: only a struct can be @checked, not a union",
            "S3233 checked: 'U' has no fields to check",
            "S3233 checked: 'S' has no fields to check",
        ], Messages(result));
        Assert.All(result.Diagnostics, d => Assert.Equal("@checked", d.Tree.Text.ToString(d.Diagnostic.Span)));
        Assert.True(result.HasErrors);
    }

    [Theory]
    [InlineData("import Sable.IO", "a generator cannot import 'Sable.IO'")]
    [InlineData("fn helper() { print(\"hi\") }", "a generator cannot use 'print'")]
    [InlineData("fn helper() -> String? => Runtime.readFile(path: \"x\")", "a generator cannot use 'Runtime'")]
    [InlineData("#if DEBUG\nfn helper() { }\n#endif", "a generator has no conditional compilation")]
    public void AGeneratorThatCouldReadWriteOrWaitIsRejected(string extra, string expected) {
        var imports = extra.StartsWith("import", StringComparison.Ordinal) ? extra + "\n" : "";
        var body = imports.Length > 0 ? "" : extra;
        var set = Set(("generators/bad.sb", $$"""
            module Bad

            import Sable.Generators
            {{imports}}

            @generator("bad")
            fn bad(_ d: Declaration, into output: inout Output) { }

            {{body}}
            """));
        var result = Run(set, "module App\n\n@bad\nstruct S { }\n");
        var message = Assert.Single(Messages(result));
        Assert.StartsWith("S3230 " + expected, message);
        Assert.Equal("generators/bad.sb", result.Diagnostics[0].Tree.Text.FileName);
    }

    [Theory]
    [InlineData("@generator(\"x\")\nfn g(_ d: Declaration) { }", "a generator is declared 'fn name(_ declaration: Declaration, into output: inout Output)'")]
    [InlineData("@generator(\"x\")\nfn g(_ d: Declaration, into output: Output) { }", "a generator is declared")]
    [InlineData("@generator(\"x\")\nfn g(_ d: Declaration, into output: inout Output) -> Int { return 1 }", "a generator is declared")]
    [InlineData("@generator(name)\nfn g(_ d: Declaration, into output: inout Output) { }", "a generator is named by a string")]
    [InlineData("@generator(\"derive\")\nfn g(_ d: Declaration, into output: inout Output) { }", "'@derive' is the compiler's own attribute")]
    [InlineData("@generator(\"x\")\nfn g(_ d: Declaration, into output: inout Output) { }\n@generator(\"x\")\nfn h(_ d: Declaration, into output: inout Output) { }",
        "a generator called 'x' is already declared, in this package")]
    public void AGeneratorsDeclarationIsChecked(string declarations, string expected) {
        var set = Set(("generators/g.sb", $"module G\n\nimport Sable.Generators\n\n{declarations}\n"));
        var result = Run(set, "module App\n\n@x\n@derive(Equatable)\nstruct S { }\n");
        Assert.Contains(Messages(result), m => m.StartsWith("S3230 " + expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TwoPackagesGeneratorsCannotAnswerToOneAttribute() {
        var one = GeneratorSet.FromSources("one", [("one/generators/g.sb", "module One\n\nimport Sable.Generators\n\n@generator(\"x\")\nfn g(_ d: Declaration, into output: inout Output) { }\n")]);
        var two = GeneratorSet.FromSources("two", [("two/generators/g.sb", "module Two\n\nimport Sable.Generators\n\n@generator(\"x\")\nfn g(_ d: Declaration, into output: inout Output) { }\n")]);
        var result = SourceGenerators.Run("app", [one, two], [SyntaxTree.Parse(new SourceText("module App\n\n@x\nstruct S { }\n", "src/main.sb"))]);
        Assert.Equal(["S3230 a generator called 'x' is already declared, in package 'one': two generators cannot answer to one attribute"], Messages(result));
    }

    [Fact]
    public void ATrapIsReportedAtTheAttributeAndTheOtherDeclarationsStillRun() {
        var set = Set(("generators/t.sb", """
            module T

            import Sable.Generators

            @generator("t")
            fn t(_ d: Declaration, into output: inout Output) {
                let first = d.fields[0]
                output.add(file: "\(d.name).t.sb", "// \(first.name)")
            }
            """));
        var result = Run(set, "module App\n\n@t\nstruct A { var a: Int }\n\n@t\nstruct Empty { }\n\n@t\nstruct C { var c: Int }\n");
        var message = Assert.Single(Messages(result));
        Assert.StartsWith("S3231 the generator 't' trapped on 'Empty': ", message);
        Assert.Equal("@t", result.Diagnostics[0].Tree.Text.ToString(result.Diagnostics[0].Diagnostic.Span));
        Assert.Equal(["A.t.sb", "C.t.sb"], result.Files.Select(static f => f.FileName));
    }

    [Fact]
    public void AGeneratorThatNeverStopsRunsOutOfSteps() {
        var set = Set(("generators/l.sb", """
            module L

            import Sable.Generators

            @generator("loop")
            fn loop(_ d: Declaration, into output: inout Output) {
                var i = 0
                while true {
                    i = i + 1
                }
            }
            """));
        var result = SourceGenerators.Run("app", [set], [SyntaxTree.Parse(new SourceText("module App\n\n@loop\nstruct S { }\n", "src/main.sb"))], stepLimit: 100_000);
        Assert.Equal(["S3231 the generator 'loop' took more than 100,000 steps for 'S': does it loop forever?"], Messages(result));
    }

    [Theory]
    [InlineData("../escape.sb", "a name is one file's")]
    [InlineData("nested/x.sb", "a name is one file's")]
    [InlineData("x.txt", "a source file's name ends in '.sb'")]
    [InlineData("", "a file needs a name")]
    public void AGeneratedFileNeedsAPlainName(string name, string expected) {
        var set = Set(("generators/n.sb", $$"""
            module N

            import Sable.Generators

            @generator("n")
            fn n(_ d: Declaration, into output: inout Output) {
                output.add(file: "{{name}}", "")
            }
            """));
        var result = Run(set, "module App\n\n@n\nstruct S { }\n");
        Assert.StartsWith($"S3234 the generator 'n' wrote a file called '{name}': {expected}", Assert.Single(Messages(result)));
        Assert.Empty(result.Files);
    }

    [Fact]
    public void TwoDeclarationsCannotGetOneFileNameAndAFileHasNoModuleLine() {
        var set = Set(("generators/s.sb", """
            module S

            import Sable.Generators

            @generator("same")
            fn same(_ d: Declaration, into output: inout Output) {
                output.add(file: "same.sb", "// \(d.name)")
                output.add(file: "\(d.name).module.sb", "module Elsewhere\n")
            }
            """));
        var result = Run(set, "module App\n\n@same\nstruct A { }\n\n@same\nstruct B { }\n");
        Assert.Equal([
            "S3234 the generator 'same' wrote a 'module' line in 'A.module.sb': the compiler puts the file in a module itself; to choose it, say 'inModule:'",
            "S3234 the generator 'same' wrote 'same.sb' for 'A' already, in package 'app': each file it writes needs a name of its own, such as one with the type's name in it",
            "S3234 the generator 'same' wrote a 'module' line in 'B.module.sb': the compiler puts the file in a module itself; to choose it, say 'inModule:'",
        ], Messages(result));
        Assert.Equal(["same.sb"], result.Files.Select(static f => f.FileName));
    }

    [Fact]
    public void ARunIsCachedByTheDeclaration() {
        // A program of its own, so that no other test's runs are in its cache.
        var set = Set(("generators/describe.sb", Describer + "\n// cache test\n"));
        const string before = "module App\n\n@describe\nstruct A { var a: Int }\n\n@describe\nstruct B { var b: Int }\n\nfn unrelated() -> Int { return 1 }\n";
        var first = Run(set, before);
        Assert.Equal(2, first.Files.Count);

        var runs = SourceGenerators.Executions;
        var same = Run(set, before.Replace("return 1", "return 2", StringComparison.Ordinal));
        Assert.Equal(runs, SourceGenerators.Executions);
        Assert.Equal(first.Files.Select(static f => f.Text), same.Files.Select(static f => f.Text));

        var changed = Run(set, before.Replace("var b: Int", "var b: Int, var c: Int", StringComparison.Ordinal));
        Assert.True(SourceGenerators.Executions > runs);
        Assert.Contains("c: \\(self.c)", changed.Files[1].Text);
    }

    [Fact]
    public void AProgramCompiledBeforeIsReportedOnTheTreesThatAskAgain() {
        // The editor reads a generator from disk, then parses it again when
        // it is opened: the second set's text is the first's, so its program
        // comes from the cache, and the error must be on the second's tree.
        const string Text = "module Local\n\nimport Sable.Generators\n\n@generator(\"local\")\nfn local(_ d: Declaration, into output: inout Output) {\n    print(d.name)\n}\n// trees test\n";
        var first = SyntaxTree.Parse(new SourceText(Text, "generators/local.sb"));
        Assert.Contains(SourceGenerators.Check(GeneratorSet.FromTrees("app", [first])), d => d.Tree == first && d.Diagnostic.Code == ErrorCode.ERR_GeneratorRule);

        var second = SyntaxTree.Parse(new SourceText(Text, "generators/local.sb"));
        var again = SourceGenerators.Check(GeneratorSet.FromTrees("app", [second]));
        Assert.Contains(again, d => d.Diagnostic.Code == ErrorCode.ERR_GeneratorRule);
        Assert.All(again, d => Assert.Same(second, d.Tree));
    }

    [Fact]
    public void AnAttributeInAGeneratedFileStartsNoGenerator() {
        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", "",
            ("src/lib.sb", "module App\n\n@more\npublic struct S { }\n"),
            ("generators/more.sb", """
                module More

                import Sable.Generators

                @generator("more")
                fn more(_ d: Declaration, into output: inout Output) {
                    output.add(file: "\(d.name)More.sb", "@more\npublic struct \(d.name)More { }\n")
                }
                """));
        var workspace = fixture.Load("app");
        Assert.True(PackageBuilder.Check(workspace, fixture.Environment, new()) == 0, fixture.Errors.ToString());

        // What a generator wrote is compiled, but never given to a generator.
        var generated = Path.Combine(workspace.TargetDirectory, "generated", "app", "more");
        Assert.Equal(["SMore.sb"], Directory.GetFiles(generated).Select(Path.GetFileName));
    }

    // ---- Where messages go ------------------------------------------------------

    [Fact]
    public void AMessageCanPointAtAFieldACaseOrAParameter() {
        var set = Set(("generators/v.sb", """
            module V

            import Sable.Generators

            @generator("checked")
            fn check(_ d: Declaration, into output: inout Output) {
                for field in d.fields {
                    if field.type == "Int" {
                        output.error("'\(field.name)' cannot be an Int", at: field.location)
                    }
                }
                for c in d.cases {
                    output.warning("case \(c.name)", at: c.location)
                }
                for p in d.parameters {
                    output.warning("parameter \(p.name)", at: p.location)
                }
                output.warning("about \(d.name)", at: d.location)
            }
            """));
        var result = Run(set, """
            module App

            @checked
            struct S {
                var ok: String
                var bad: Int
            }

            @checked
            union U { A, B(Int) }

            @checked
            fn f(x: Int, _ y: String) { }
            """);
        var placed = result.Diagnostics.Select(static d => $"{d.Diagnostic.Id} {d.Tree.Text.ToString(d.Diagnostic.Span)}: {d.Diagnostic.Message}").ToList();
        Assert.Equal([
            "S3232 bad: checked: 'bad' cannot be an Int",
            "S3233 S: checked: about S",
            "S3233 A: checked: case A",
            "S3233 B: checked: case B",
            "S3233 U: checked: about U",
            "S3233 x: checked: parameter x",
            "S3233 y: checked: parameter y",
            "S3233 f: checked: about f",
        ], placed);
    }

    // ---- Resolved types -----------------------------------------------------------

    /// <summary>A generator that writes what it was given about types as comments.</summary>
    const string Types = """
        module Types

        import Sable.Generators
        import Sable.Text

        fn shape(_ t: TypeRef) -> String {
            var arguments: String[>..] = []
            for a in t.arguments {
                arguments.append(shape(a))
            }
            var text = "\(t.kind):\(t.name)"
            if !t.moduleName.isEmpty {
                text = text + "@" + t.moduleName
            }
            if !arguments.isEmpty {
                text = text + "<" + arguments.joined(separator: ",") + ">"
            }
            return text
        }

        @generator("types")
        fn types(_ d: Declaration, into output: inout Output) {
            let t = TextBuilder()
            t.appendLine("// \(d.keyword) \(d.qualifiedName) all=[\(d.allConformances.joined(separator: ","))] super=\(d.superclass ?? "-")")
            for f in d.fields {
                t.appendLine("// field \(f.name) \(shape(f.resolved)) json=\(f.resolved.has(attribute: "types")) eq=\(f.resolved.conforms(to: "Equatable"))")
            }
            for c in d.cases {
                for p in c.payload {
                    t.appendLine("// payload \(c.name).\(p.name) \(shape(p.resolved))")
                }
            }
            for p in d.parameters {
                t.appendLine("// parameter \(p.label)/\(p.name) \(shape(p.resolved)) default=\(p.hasDefault)")
            }
            if let r = d.returned {
                t.appendLine("// returns \(d.returnType) \(shape(r)) async=\(d.isAsync) throws=\(d.isThrowing)")
            }
            let file = (d.qualifiedName.split(".")).joined(separator: "_")
            output.add(file: "\(file).types.sb", t.build())
        }
        """;

    [Fact]
    public void TypesAreGivenResolved() {
        var result = Run(Set(("generators/types.sb", Types)), """
            module App

            protocol Named { fn name() -> String }

            @types
            struct Point: Equatable {
                var x: Int
                var y: Int
            }

            class Base { }

            @types
            final class Shape: Base {
                var points: Point[>..] = []
                var label: String?
                var tags: [String: Int] = [:]
                var pair: (Int, Bool) = (0, false)
            }

            extension Shape: Named {
                fn name() -> String => "shape"
            }

            @types
            union Event {
                Moved(by: Point),
                Renamed(String)
            }

            @types
            fn scale(_ point: Point, by factor: Int = 2) throws -> Point? {
                return none
            }
            """);
        Assert.False(result.HasErrors, string.Join("\n", Messages(result)));
        string Of(string name) => Comments(result.Files.Single(f => f.FileName == name));
        Assert.Contains("Equatable", Of("Point.types.sb").Split('\n')[0]);
        Assert.True(Of("Point.types.sb").Contains("// field x struct:Int@Sable.Core json=false eq=true\n// field y struct:Int@Sable.Core json=false eq=true", StringComparison.Ordinal),
            string.Join("\n----\n", result.Files.Select(Comments)));
        Assert.Contains("// class Shape all=[", Of("Shape.types.sb"));
        Assert.Contains("Named", Of("Shape.types.sb").Split('\n')[0]);
        Assert.Contains("super=Base", Of("Shape.types.sb"));
        Assert.Contains("// field points array:Array<struct:Point@App> json=false eq=", Of("Shape.types.sb"));
        Assert.Contains("// field label optional:Optional<struct:String@Sable.Core>", Of("Shape.types.sb"));
        Assert.Contains("// field tags dictionary:Dictionary@Sable.Core<struct:String@Sable.Core,struct:Int@Sable.Core>", Of("Shape.types.sb"));
        Assert.Contains("// field pair tuple:<struct:Int@Sable.Core,struct:Bool@Sable.Core>", Of("Shape.types.sb"));
        Assert.Contains("// payload Moved.by struct:Point@App", Of("Event.types.sb"));
        Assert.Contains("// payload Renamed. struct:String@Sable.Core", Of("Event.types.sb"));
        Assert.Equal("""
            // fn scale all=[] super=-
            // parameter /point struct:Point@App default=false
            // parameter by/factor struct:Int@Sable.Core default=true
            // returns Point? optional:Optional<struct:Point@App> async=false throws=true
            """, Of("scale.types.sb"));
    }

    [Fact]
    public void AFieldsTypeShowsItsOwnAttributes() {
        var result = Run(Set(("generators/types.sb", Types)), """
            module App

            @types
            struct Inner { var a: Int }

            @types
            struct Outer { var inner: Inner }
            """);
        Assert.Contains("// field inner struct:Inner@App json=true", Comments(result.Files.Single(static f => f.FileName == "Outer.types.sb")));
    }

    [Fact]
    public void NestedTypesFunctionsAndMethodsAreGiven() {
        var result = Run(Set(("generators/types.sb", Types)), """
            module App

            struct Outer {
                @types
                struct Inner { var a: Int }

                @types
                static fn make() -> Outer { return Outer() }
            }

            extension Outer {
                @types
                fn describe(verbose: Bool) -> String { return "" }
            }

            @types
            fn top() { }
            """);
        Assert.False(result.HasErrors, string.Join("\n", Messages(result)));
        Assert.Equal(["Outer_Inner.types.sb", "Outer_make.types.sb", "Outer_describe.types.sb", "top.types.sb"], result.Files.Select(static f => f.FileName));
        Assert.Contains("// struct Outer.Inner", Comments(result.Files[0]));
        Assert.Contains("// fn Outer.make", Comments(result.Files[1]));
        Assert.Contains("// parameter verbose/verbose struct:Bool@Sable.Core default=false", Comments(result.Files[2]));
        Assert.DoesNotContain("returns", Comments(result.Files[3]));
    }

    [Fact]
    public void AGeneratorsAttributeIsAcceptedOnNestedTypesFunctionsMethodsAndParameters() {
        var set = Set(("generators/types.sb", Types.Replace("@generator(\"types\")", "@generator(\"types\", members: [\"note\"])", StringComparison.Ordinal)));
        var options = ParseOptions.Default with { GeneratorAttributes = [..set.Attributes.Types], GeneratorMemberAttributes = [..set.Attributes.Members] };
        var tree = SyntaxTree.Parse(new SourceText("""
            module App

            struct Outer {
                @types
                struct Inner { }

                @types
                fn method(@note x: Int) { }
            }

            @types
            fn top() { }

            fn main() { }
            """, "src/main.sb"), options);
        var result = SourceGenerators.Run("app", [set], [tree]);
        var compiled = Pipeline.Compile([tree, ..result.Files.Select(f => SyntaxTree.Parse(new SourceText(f.Text, f.FileName), options))]);
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors));
    }

    // ---- Package generators ---------------------------------------------------------

    const string Routes = """
        module Routes

        import Sable.Generators
        import Sable.Text

        @generator("route", members: ["param"])
        fn routes(_ pkg: Package, into output: inout Output) {
            let t = TextBuilder()
            t.appendLine("public fn allRoutes() -> String[>..] {")
            t.appendLine("    var routes: String[>..] = []")
            for d in pkg.declarations {
                let path = d.trigger.string(named: "path") ?? "/"
                t.appendLine("    routes.append(\"\(path) -> \(d.qualifiedName)\")")
                if d.keyword != "fn" {
                    output.warning("a route is usually a function", at: d.location)
                }
            }
            t.appendLine("    return routes")
            t.appendLine("}")
            output.add(file: "Routes.sb", t.build())
        }

        @generator("schema", always: true)
        fn schema(_ pkg: Package, into output: inout Output) {
            let t = TextBuilder()
            t.appendLine("// package \(pkg.name), \(pkg.declarations.count) declaration(s)")
            for input in pkg.inputs {
                t.appendLine("public fn schema_\((input.path.split("/")).joined(separator: "_"))() -> String => \"\(input.text.trimmedEnd())\"")
            }
            output.add(file: "Schema.sb", t.build(), inModule: pkg.inputs.isEmpty ? "" : "App.Data")
        }
        """;

    [Fact]
    public void APackageGeneratorIsGivenEveryDeclarationAtOnce() {
        var set = Set(("generators/routes.sb", Routes));
        var options = ParseOptions.Default with { GeneratorAttributes = [..set.Attributes.Types], GeneratorMemberAttributes = [..set.Attributes.Members] };
        var first = SyntaxTree.Parse(new SourceText("module App\n\n@route(path: \"/users\")\nfn users() { }\n\nfn main() {\n    for r in allRoutes() {\n        print(r)\n    }\n}\n", "src/main.sb"), options);
        var second = SyntaxTree.Parse(new SourceText("module App\n\n@route(path: \"/health\")\nstruct Health { }\n", "src/health.sb"), options);
        var result = SourceGenerators.Run("app", [set], [first, second]);
        Assert.Equal(["S3233 route: a route is usually a function"], Messages(result));
        Assert.Equal("Health", result.Diagnostics[0].Tree.Text.ToString(result.Diagnostics[0].Diagnostic.Span));
        Assert.Equal(["Routes.sb", "Schema.sb"], result.Files.Select(static f => f.FileName).Order());
        Assert.Contains("// Written by the generator 'route' for 2 declaration(s).", result.Files.Single(static f => f.FileName == "Routes.sb").Text);

        var compiled = Pipeline.Compile([first, second, ..result.Files.Select(f => SyntaxTree.Parse(new SourceText(f.Text, f.FileName), options))]);
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors));
        var interpreter = new SilInterpreter(compiled.Sil!);
        interpreter.Run(compiled.Sil!.EntryPoint!);
        Assert.Equal("/users -> users\n/health -> Health\n", interpreter.Output.ToString());
    }

    [Fact]
    public void AnAlwaysGeneratorRunsWithNoDeclarationAndIsGivenTheInputs() {
        var set = Set(("generators/routes.sb", Routes));
        var lib = SyntaxTree.Parse(new SourceText("module App\n\npublic fn nothing() { }\n", "src/lib.sb"));
        var data = SyntaxTree.Parse(new SourceText("module App.Data\n", "src/data.sb"));
        var none = SourceGenerators.Run(new GenerationInput("app", [set], [lib, data]));
        var file = Assert.Single(none.Files);
        Assert.Equal("Schema.sb", file.FileName);
        Assert.Same(lib, file.Origin);
        Assert.Contains("module App\n", file.Text);
        Assert.Contains("// package app, 0 declaration(s)", file.Text);

        var withInputs = SourceGenerators.Run(new GenerationInput("app", [set], [lib, data]) {
            Inputs = [new("schemas/user", "name\n"), new("schemas/item", "id\n")],
        });
        var schema = Assert.Single(withInputs.Files);
        Assert.Contains("module App.Data\n", schema.Text);
        Assert.Contains("public fn schema_schemas_user() -> String => \"name\"", schema.Text);
        Assert.Contains("public fn schema_schemas_item() -> String => \"id\"", schema.Text);

        // A change to an input is a change to the run's input.
        var runs = SourceGenerators.Executions;
        SourceGenerators.Run(new GenerationInput("app", [set], [lib, data]) { Inputs = [new("schemas/user", "name\n"), new("schemas/item", "id\n")] });
        Assert.Equal(runs, SourceGenerators.Executions);
        SourceGenerators.Run(new GenerationInput("app", [set], [lib, data]) { Inputs = [new("schemas/user", "email\n")] });
        Assert.Equal(runs + 1, SourceGenerators.Executions);
    }

    [Fact]
    public void AFileCanOnlyGoInAModuleThePackageDeclares() {
        var set = Set(("generators/m.sb", """
            module M

            import Sable.Generators

            @generator("m")
            fn m(_ d: Declaration, into output: inout Output) {
                output.add(file: "x.sb", "", inModule: "Elsewhere")
            }
            """));
        var result = Run(set, "module App\n\n@m\nstruct S { }\n");
        Assert.Equal(["S3234 the generator 'm' wrote 'x.sb' in module 'Elsewhere', which package 'app' does not declare: a generated file goes in one of the package's own modules"], Messages(result));
    }

    [Fact]
    public void AlwaysIsOnlyForAPackageGenerator() {
        var set = Set(("generators/a.sb", "module A\n\nimport Sable.Generators\n\n@generator(\"a\", always: true)\nfn a(_ d: Declaration, into output: inout Output) { }\n"));
        var result = Run(set, "module App\n\n@a\nstruct S { }\n");
        Assert.StartsWith("S3230 'always: true' is for a generator given the whole package", Assert.Single(Messages(result)));
    }

    // ---- In packages ----------------------------------------------------------

    /// <summary>A workspace of 'describe', with the generator, 'app', which depends on it, and 'top', which depends on 'app'.</summary>
    static void Packages(PackageFixture fixture, string main = "") {
        fixture.Package("describe", "describe", "1.0.0", "", ("src/lib.sb", "module Describe\n\npublic fn version() -> Int { return 1 }\n"), ("generators/describe.sb", Describer));
        fixture.Package("app", "app", "1.0.0", "[dependencies]\ndescribe = { path = \"../describe\" }\n",
            ("src/lib.sb", "module App\n\n@describe\npublic struct Point {\n    public var x: Int\n    public var y: Int\n}\n\npublic fn origin() -> String { return Point(x: 0, y: 0).describe() }\n"));
        fixture.Package("top", "top", "1.0.0", "[dependencies]\napp = { path = \"../app\" }\n",
            ("src/main.sb", main.Length > 0 ? main : "module Top\n\nimport App\n\nfn main() {\n    print(origin())\n}\n"));
    }

    [Fact]
    public void ADependentPackagesTypesGetTheGeneratedCode() {
        using var fixture = new PackageFixture();
        Packages(fixture);
        var workspace = fixture.Load("top");
        Assert.True(PackageBuilder.Check(workspace, fixture.Environment, new()) == 0, fixture.Errors.ToString());
        var generated = Path.Combine(workspace.TargetDirectory, "generated", "app", "describe", "Point.describe.sb");
        Assert.True(File.Exists(generated), fixture.Errors.ToString());
        Assert.Contains("module App\n", File.ReadAllText(generated));

        if (Programs.CanBuild) {
            var binary = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new())!);
            Assert.Equal("Point { x: 0, y: 0 }\n", PackageFixture.Execute(binary.Path).Output);
        }
    }

    [Fact]
    public void ATransitiveDependencysGeneratorsDoNotApply() {
        using var fixture = new PackageFixture();
        Packages(fixture, "module Top\n\nimport App\n\n@describe\nstruct Local { var a: Int }\n\nfn main() {\n    print(Local(a: 1).describe())\n}\n");
        var workspace = fixture.Load("top");
        Assert.Equal(1, PackageBuilder.Check(workspace, fixture.Environment, new()));
        Assert.Contains("there is no attribute '@describe'", fixture.Errors.ToString());
        Assert.False(Directory.Exists(Path.Combine(workspace.TargetDirectory, "generated", "top")));
    }

    [Fact]
    public void AGeneratorsAttributesAreAcceptedOnlyWhereItReadsThem() {
        using var fixture = new PackageFixture();
        Packages(fixture);
        fixture.Write("app/src/more.sb", "module App\n\nprotocol P {\n    @describe fn inAProtocol()\n}\n\nstruct Holder {\n    @describe var a: Int\n    @hidden var b: Int\n}\n\n@hidden\nstruct Wrong { }\n");
        Assert.Equal(1, PackageBuilder.Check(fixture.Load("top"), fixture.Environment, new()));
        var errors = fixture.Errors.ToString();
                Assert.Contains("'@describe' starts a source generator, which is given a type, a function or a method, not a property", errors);
        Assert.Contains("src/more.sb:4:5", errors);
        Assert.Contains("'@hidden' is read by a source generator on a field, a case, an initialiser, a method or a parameter, not on a struct", errors);
        Assert.DoesNotContain("src/more.sb:9:", errors);
    }

    [Fact]
    public void AGeneratorsErrorStopsTheBuildAtTheAttribute() {
        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", "",
            ("src/main.sb", "module App\n\n@strict\nstruct S { }\n\nfn main() { }\n"),
            ("generators/strict.sb", "module Strict\n\nimport Sable.Generators\n\n@generator(\"strict\")\nfn strict(_ d: Declaration, into output: inout Output) {\n    output.error(\"no fields\")\n}\n"));
        var workspace = fixture.Load("app");
        Assert.Equal(1, PackageBuilder.Check(workspace, fixture.Environment, new()));
        Assert.Contains("error[S3232]: strict: no fields\n --> src/main.sb:3:1", fixture.Errors.ToString());
        Assert.Null(PackageBuilder.Build(workspace, fixture.Environment, new()));
    }

    [Fact]
    public void AFileNoLongerGeneratedIsRemoved() {
        using var fixture = new PackageFixture();
        Packages(fixture);
        var workspace = fixture.Load("top");
        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));
        var generated = Path.Combine(workspace.TargetDirectory, "generated", "app", "describe", "Point.describe.sb");
        Assert.True(File.Exists(generated));

        fixture.Write("app/src/lib.sb", "module App\n\npublic fn origin() -> String { return \"none\" }\n");
        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));
        Assert.False(File.Exists(generated));
    }

    [Fact]
    public void ADependencysTypesResolve() {
        using var fixture = new PackageFixture();
        fixture.Package("gen", "gen", "1.0.0", "", ("generators/types.sb", Types));
        fixture.Package("base", "base", "1.0.0", "[dependencies]\ngen = { path = \"../gen\" }\n",
            ("src/lib.sb", "module Base\n\n@types\npublic struct Id {\n    public var value: Int\n}\n"));
        fixture.Package("app", "app", "1.0.0", "[dependencies]\ngen = { path = \"../gen\" }\nbase = { path = \"../base\" }\n",
            ("src/lib.sb", "module App\n\nimport Base\n\n@types\npublic struct User {\n    public var id: Id\n}\n"));
        var workspace = fixture.Load("app");
        Assert.True(PackageBuilder.Check(workspace, fixture.Environment, new()) == 0, fixture.Errors.ToString());
        var user = File.ReadAllText(Path.Combine(workspace.TargetDirectory, "generated", "app", "types", "User.types.sb"));
        Assert.Contains("// field id struct:Id@Base json=true", user);
    }

    [Fact]
    public void AManifestCanListInputsForItsGenerators() {
        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", "[generators]\ninputs = [\"schemas\"]\n",
            ("src/lib.sb", "module App\n"),
            ("src/data.sb", "module App.Data\n"),
            ("schemas/user", "name\n"),
            ("generators/routes.sb", Routes));
        var workspace = fixture.Load("app");
        Assert.True(PackageBuilder.Check(workspace, fixture.Environment, new()) == 0, fixture.Errors.ToString());
        var schema = File.ReadAllText(Path.Combine(workspace.TargetDirectory, "generated", "app", "schema", "Schema.sb"));
        Assert.Contains("public fn schema_schemas_user() -> String => \"name\"", schema);

        fixture.Write("app/sable.toml", "[package]\nname = \"app\"\nversion = \"1.0.0\"\n[generators]\ninputs = [\"missing\"]\n");
        Assert.Equal(1, PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new()));
        Assert.Contains("the generator input 'missing' that its manifest lists does not exist", fixture.Errors.ToString());
    }

    // ---- Snapshot tests ---------------------------------------------------------

    [Fact]
    public void ASnapshotCaseIsBlessedThenCompared() {
        using var fixture = new PackageFixture();
        var directory = fixture.Package("describe", "describe", "1.0.0", "",
            ("generators/describe.sb", Describer),
            ("generators/tests/basic/model.sb", "module Model\n\n@describe\nstruct Point {\n    var x: Int\n    @hidden var y: Int\n}\n"));
        var output = new StringWriter();

        // Nothing expected yet: the written file is a difference.
        var first = GeneratorSnapshots.Run("describe", directory, bless: false, output)!;
        Assert.Equal((0, 1), (first.Passed, first.Failed));
        Assert.Contains("describe/Point.describe.sb: written, but not expected:", output.ToString());

        Assert.Equal(1, GeneratorSnapshots.Run("describe", directory, bless: true, new StringWriter())!.Blessed);
        var expected = Path.Combine(directory, "generators", "tests", "basic", "expected", "describe", "Point.describe.sb");
        Assert.Contains("return \"Point { x: \\(self.x) }\"", File.ReadAllText(expected));
        Assert.False(File.Exists(Path.Combine(directory, "generators", "tests", "basic", "expected", "diagnostics.txt")));
        Assert.Equal((1, 0), (GeneratorSnapshots.Run("describe", directory, bless: false, new StringWriter())! is var again ? (again.Passed, again.Failed) : default));

        // The generator changes: the case fails, and says where.
        fixture.Write("describe/generators/describe.sb", Describer.Replace("{ \\(all) }", "[\\(all)]", StringComparison.Ordinal));
        output = new StringWriter();
        Assert.Equal(1, GeneratorSnapshots.Run("describe", directory, bless: false, output)!.Failed);
        Assert.Contains("describe/Point.describe.sb differs at line", output.ToString());
    }

    [Fact]
    public void ASnapshotCaseMustCompileWithWhatWasWritten() {
        using var fixture = new PackageFixture();
        var directory = fixture.Package("bad", "bad", "1.0.0", "",
            ("generators/bad.sb", "module Bad\n\nimport Sable.Generators\n\n@generator(\"bad\")\nfn bad(_ d: Declaration, into output: inout Output) {\n    output.add(file: \"\\(d.name).bad.sb\", \"fn broken() -> Int { return \\\"text\\\" }\\n\")\n}\n"),
            ("generators/tests/one/model.sb", "module Model\n\n@bad\nstruct S { }\n"));
        GeneratorSnapshots.Run("bad", directory, bless: true, new StringWriter());
        var diagnostics = File.ReadAllText(Path.Combine(directory, "generators", "tests", "one", "expected", "diagnostics.txt"));
        Assert.StartsWith("error S3004 expected/bad/S.bad.sb:", diagnostics);
    }
}
