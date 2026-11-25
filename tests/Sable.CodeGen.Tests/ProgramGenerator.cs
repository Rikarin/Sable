using System.Globalization;
using System.Text;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Small programs that type-check, end and print what they worked out, for
///     differential fuzzing: the same program compiled or run two ways must
///     print the same and trap the same. A program is what a series of bytes
///     chooses, so that a fuzzer's input is one and its mutants are programs
///     too.
/// </summary>
/// <remarks>
///     A program is a few functions over numbers of several widths, text,
///     arrays, optionals, a struct, a union, a class with a base class, a
///     protocol and generics over it. Each function calls only those before
///     it, and loops are short, so every program ends. Arithmetic wraps or is
///     kept small most of the time and traps now and then, as a program may.
/// </remarks>
public sealed class ProgramGenerator {
    readonly byte[] _choices;
    readonly StringBuilder _text = new();
    readonly List<(string Name, string[] Parameters, string Returns, bool Throws)> _functions = [];
    readonly List<(string Name, string Type, bool Mutable)> _scope = [];
    int _at;
    int _names;
    int _indent;
    int _loops;
    bool _throws;
    // How many statements may still be written: a program is small whatever its bytes.
    int _left = 60;

    ProgramGenerator(byte[] choices) => _choices = choices;

    /// <summary>The program the bytes choose: after the last of them, the simplest of everything.</summary>
    public static string Generate(ReadOnlySpan<byte> choices) {
        var generator = new ProgramGenerator(choices.ToArray());
        return generator.Program();
    }

    /// <summary>A program at random: of so many choices.</summary>
    public static byte[] Choices(int seed, int count = 400) {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    int Next(int below) => _at < _choices.Length ? _choices[_at++] % below : 0;
    bool Chance(int outOf) => Next(outOf) == outOf - 1;
    T Pick<T>(params T[] all) => all[Next(all.Length)];
    string Fresh(string kind) => kind + (_names++).ToString(CultureInfo.InvariantCulture);

    static readonly string[] Types = ["Int", "Bool", "String", "I32", "U8", "Int[>..]", "Int?", "Pair", "Tree", "Box", "F64", "any Sized", "(Int, String)", "Stack<Int>", "Stack<String>"];

    const string Prelude = """
        module Generated

        union Broken: Error { Small(Int), Named(String) }

        protocol Sized {
            fn size() -> Int
        }

        extension Sized {
            fn twice() -> Int { return size() &* 2 }
        }

        @derive(Equatable)
        struct Pair: Sized {
            var a: Int
            var b: String

            fn size() -> Int { return a &+ b.count }
            mutating fn grow(_ n: Int) { a = a &+ n }
        }

        union Tree {
            Leaf
            Value(Int)
            Branch(Tree[>..], String)
        }

        class Box: Sized {
            var value: Int
            var next: Box?
            init(_ value: Int) { self.value = value; next = none }
            fn size() -> Int { return value }
        }

        class Wide: Box {
            let width: Int
            init(wide value: Int, _ width: Int) { self.width = width; super.init(value) }
            override fn size() -> Int { return value &* 3 &+ width }
        }

        struct Stack<Element> {
            var items: Element[>..] = []

            mutating fn push(_ item: Element) { items.append(item) }
            mutating fn pop() -> Element? { return items.isEmpty ? none : items.removeLast() }
            fn mapped<U>(_ body: (Element) -> U) -> Stack<U> {
                var made = Stack<U>()
                for item in items { made.push(body(item)) }
                return made
            }
        }

        fn largest<T: Sized>(_ a: T, _ b: T) -> T { return a.size() >= b.size() ? a : b }
        fn through<T>(_ value: T, _ body: (T) -> T) -> T { return body(value) }
        fn with<T>(_ items: T[>..], _ item: T) -> T[>..] {
            var all = items
            all.append(item)
            return all
        }
        fn list(_ items: Int[>..]) -> Int[>..] { return items }
        fn maybe(_ value: Int) -> Int? { return value }
        fn box(_ value: Box) -> Box { return value }
        fn sized(_ value: any Sized) -> any Sized { return value }
        fn firstOr<T>(_ items: T[>..], _ other: T) -> T { return items.isEmpty ? other : items[0] }
        fn swapped<A, B>(_ a: A, _ b: B) -> (B, A) { return (b, a) }

        fn weight(_ tree: Tree) -> Int {
            match tree {
                case .Leaf => { return 1 }
                case .Value(let n) => { return n }
                case .Branch(let all, let name) => {
                    var sum = name.count
                    for one in all { sum = sum &+ weight(one) }
                    return sum
                }
            }
        }

        fn shown(_ value: Int) -> String { return "\(value)" }
        fn shown(_ value: Bool) -> String { return value ? "yes" : "no" }
        fn shown(_ value: String) -> String { return value.count > 40 ? "<\(value.count)>" : value }
        fn shown(_ value: I32) -> String { return "\(value)" }
        fn shown(_ value: U8) -> String { return "\(value)" }
        fn shown(_ value: F64) -> String { return "\(value)" }
        fn shown(_ value: Int[>..]) -> String { return "\(value)" }
        fn shown(_ value: Int?) -> String { return "\(value ?? -1) \(value == none)" }
        fn shown(_ value: Pair) -> String { return "\(value.a):\(shown(value.b))" }
        fn shown(_ value: Tree) -> String { return "t\(weight(value))" }
        fn shown(_ value: Box) -> String { return "b\(value.size()) \(value.twice()) \(value.next == none)" }
        fn shown(_ value: any Sized) -> String { return "s\(value.size()) \(value.twice())" }
        fn shown(_ value: (Int, String)) -> String { return "\(value.0);\(shown(value.1))" }
        fn shown(_ value: Stack<Int>) -> String { return "\(value.items)" }
        fn shown(_ value: Stack<String>) -> String { return "\(value.items.count)" }


        """;

    string Program() {
        _text.Append(Prelude);
        var count = 1 + Next(5);
        for (var i = 0; i < count; i++) {
            Function();
        }

        _text.Append("fn main() {\n");
        _indent = 1;
        _scope.Clear();
        foreach (var function in _functions) {
            for (var calls = 1 + Next(2); calls > 0; calls--) {
                Print(Call(function, 0), function.Returns, function.Throws);
            }
        }

        _text.Append("}\n");
        return _text.ToString();
    }

    void Function() {
        var name = Fresh("f");
        var parameters = Enumerable.Range(0, Next(4)).Select(_ => Pick(Types)).ToArray();
        var returns = Pick(Types);
        _throws = Chance(4);
        _scope.Clear();
        var written = parameters.Select(t => (Name: Fresh("p"), Type: t)).ToList();
        foreach (var parameter in written) {
            _scope.Add((parameter.Name, parameter.Type, false));
        }

        _text.Append(CultureInfo.InvariantCulture, $"fn {name}({string.Join(", ", written.Select(static p => $"_ {p.Name}: {p.Type}"))}){(_throws ? " throws(Broken)" : "")} -> {returns} {{\n");
        _indent = 1;
        Statements(2 + Next(6));
        Line($"return {Expression(returns, 3)}");
        _text.Append("}\n\n");
        _functions.Add((name, parameters, returns, _throws));
        _throws = false;
    }

    void Line(string text) => _text.Append(' ', _indent * 4).Append(text).Append('\n');

    void Statements(int count) {
        var outer = _scope.Count;
        for (; count > 0 && _left > 0; count--) {
            _left--;
            Statement();
        }

        _scope.RemoveRange(outer, _scope.Count - outer);
    }

    void Block(string head, int count) {
        Line(head + " {");
        _indent++;
        Statements(count);
        _indent--;
        Line("}");
    }

    void Statement() {
        switch (Next(14)) {
            case 0 or 1 or 2: {
                var type = Pick(Types);
                var name = Fresh("v");
                var value = Expression(type, 3);
                Line($"var {name}: {type} = {value}");
                _scope.Add((name, type, true));
                break;
            }
            case 3 or 4 when Variables(static v => v.Mutable) is { Count: > 0 } targets: {
                var (name, type, _) = targets[Next(targets.Count)];
                switch (type) {
                    case "Int[>..]" when Chance(2): Line($"{name}.append({Expression("Int", 2)})"); break;
                    case "Pair" when Chance(2): Line($"{name}.grow({Expression("Int", 2)})"); break;
                    case "Stack<Int>" when Chance(2): Line(Chance(3) ? $"_ = {name}.pop()" : $"{name}.push({Expression("Int", 2)})"); break;
                    case "Stack<String>" when Chance(2): Line($"{name}.push({Expression("String", 2)})"); break;
                    case "Box" when Chance(2): Line($"{name}.value = {Expression("Int", 2)}"); break;
                    case "Box" when Chance(2): Line($"{name}.next = {Pick("none", $"Box({Expression("Int", 1)})")}"); break;
                    case "Int": Line($"{name} = {name} {Pick("&+", "&*", "&-", "^")} ({Expression("Int", 2)})"); break;
                    default: Line($"{name} = {Expression(type, 3)}"); break;
                }

                break;
            }
            case 5:
                Block($"if {Expression("Bool", 2)}", 1 + Next(3));
                if (Chance(2)) {
                    Block("else", 1 + Next(2));
                }

                break;
            case 6 when _loops < 2: {
                var name = Fresh("i");
                _loops++;
                _scope.Add((name, "Int", false));
                Block($"for {name} in 0..<{1 + Next(4)}", 1 + Next(3));
                _scope.RemoveAll(v => v.Name == name);
                _loops--;
                break;
            }
            case 7 when _loops < 2: {
                var name = Fresh("e");
                _loops++;
                var all = Expression("Int[>..]", 1);
                _scope.Add((name, "Int", false));
                Block($"for {name} in {all}.prefix(4)", 1 + Next(2));
                _scope.RemoveAll(v => v.Name == name);
                _loops--;
                break;
            }
            case 8: {
                var name = Fresh("o");
                Line($"if let {name} = {Expression("Int?", 2)} {{");
                _indent++;
                _scope.Add((name, "Int", false));
                Statements(1 + Next(2));
                _scope.RemoveAll(v => v.Name == name);
                _indent--;
                Line("}");
                break;
            }
            case 9: {
                var (value, text) = (Fresh("n"), Fresh("s"));
                Line($"match {Expression("Tree", 2)} {{");
                _indent++;
                Block("case .Leaf =>", 1);
                _scope.Add((value, "Int", false));
                Block($"case .Value(let {value}) =>", 1 + Next(2));
                _scope.RemoveAll(v => v.Name == value);
                _scope.Add((text, "String", false));
                Block($"case .Branch(_, let {text}) =>", 1 + Next(2));
                _scope.RemoveAll(v => v.Name == text);
                _indent--;
                Line("}");
                break;
            }
            case 10: {
                // Caught here, and shown.
                Line("do {");
                _indent++;
                var before = _throws;
                _throws = true;
                Statements(1 + Next(2));
                Line($"if {Expression("Bool", 1)} {{ throw {Error()} }}");
                _throws = before;
                _indent--;
                Line("} catch {");
                _indent++;
                Line("match error {");
                Line("    case .Small(let n) => { print(\"small \\(n)\") }");
                Line("    case .Named(let s) => { print(\"named \\(shown(s))\") }");
                Line("}");
                _indent--;
                Line("}");
                break;
            }
            case 11 when _throws:
                Line($"if {Expression("Bool", 2)} {{ throw {Error()} }}");
                break;
            case 12: {
                // A closure that keeps what it is made with.
                var name = Fresh("c");
                var body = Expression("Int", 2);
                Line($"let {name}: (Int) -> Int = (x) => x &+ ({body})");
                Print($"{name}({Expression("Int", 1)}) &+ {name}(1)", "Int", false);
                break;
            }
            default: {
                var type = Pick(Types);
                Print(Expression(type, 3), type, false);
                break;
            }
        }
    }

    string Error() => Chance(2) ? $"Broken.Named({Expression("String", 1)})" : $"Broken.Small({Expression("Int", 1)})";

    void Print(string value, string type, bool throws) {
        _ = type;
        if (!throws) {
            Line($"print(shown({value}))");
            return;
        }

        Line("do {");
        Line($"    print(shown(try {value}))");
        Line("} catch {");
        Line("    print(\"threw \\(error)\")");
        Line("}");
    }

    List<(string Name, string Type, bool Mutable)> Variables(Func<(string Name, string Type, bool Mutable), bool> which) => [.._scope.Where(which)];

    string Call((string Name, string[] Parameters, string Returns, bool Throws) function, int depth) =>
        $"{function.Name}({string.Join(", ", function.Parameters.Select(p => Expression(p, depth)))})";

    string Expression(string type, int depth) {
        // A variable of the type, when there is one; a call of a function of it, outside an inner loop.
        if (Variables(v => v.Type == type) is { Count: > 0 } known && Chance(2)) {
            return known[Next(known.Count)].Name;
        }

        if (depth > 1 && _loops < 2 && _functions.Where(f => f.Returns == type).ToList() is { Count: > 0 } callable && Chance(4)) {
            var function = callable[Next(callable.Count)];
            var call = Call(function, 1);
            return function.Throws ? $"((try? {call}) ?? {Simple(type)})" : call;
        }

        if (depth > 0 && Chance(6)) {
            return $"({Expression("Bool", depth - 1)} ? {Expression(type, depth - 1)} : {Expression(type, depth - 1)})";
        }

        if (depth > 0 && Chance(8) && type is not ("any Sized" or "Int?")) {
            return $"through({Expression(type, depth - 1)}, (x) => x)";
        }

        return depth == 0 ? Simple(type) : Compound(type, depth - 1);
    }

    string Number() => Pick("0", "1", "2", "(-1)", "7", "100", "(-128)", "255", "65536", "9223372036854775807", "(-9223372036854775807)");

    string Simple(string type) => type switch {
        "Int" => Number(),
        "Bool" => Pick("true", "false"),
        "String" => Pick("\"\"", "\"a\"", "\"two words\"", "\"\\u{1F600}é\"", "\"a longer piece of text than fits in a few bytes\""),
        "I32" => Pick("0 as I32", "5 as I32", "(-7) as I32", "2147483647 as I32"),
        "U8" => Pick("0 as U8", "1 as U8", "200 as U8", "255 as U8"),
        "F64" => Pick("0.0", "1.5", "(-2.25)", "1e10", "0.1"),
        "Int[>..]" => Pick("list([1, 2, 3])", "list([])", "list([5])", "list([9, -9, 0, 4, 4])"),
        "Int?" => Pick("(none as Int?)", "maybe(3)"),
        "Pair" => Pick("Pair { a: 1, b: \"x\" }", "Pair { a: -4, b: \"\" }"),
        "Tree" => Pick("Tree.Leaf", "Tree.Value(6)", "Tree.Branch([.Leaf, .Value(2)], \"b\")"),
        "Box" => Pick("Box(3)", "box(Wide(wide: 2, 5))"),
        "any Sized" => Pick("sized(Pair { a: 2, b: \"yy\" })", "sized(Wide(wide: 1, 1))"),
        "(Int, String)" => "(4, \"t\")",
        "Stack<Int>" => "Stack<Int>()",
        "Stack<String>" => "Stack<String>()",
        _ => throw new InvalidOperationException(type),
    };

    string Compound(string type, int depth) {
        string E(string of) => Expression(of, depth);
        switch (type) {
            case "Int":
                return Next(16) switch {
                    0 or 1 => $"({E("Int")} {Pick("&+", "&-", "&*")} {E("Int")})",
                    2 => $"({E("Int")} {Pick("&", "|", "^")} {E("Int")})",
                    // Divided by what is never 0, most of the time.
                    3 => Chance(8) ? $"({E("Int")} / {E("Int")})" : $"({E("Int")} {Pick("/", "%")} ({E("U8")} as Int &+ 1))",
                    4 => $"({E("Int")} >> {Next(64)})",
                    5 => $"{E("String")}.count",
                    6 => $"({E("Int?")} ?? {E("Int")})",
                    7 => Chance(6) ? $"({E("Int")} + {E("Int")})" : $"weight({E("Tree")})",
                    8 => $"{E("Pair")}.size()",
                    9 => $"largest({E("Box")}, {E("Box")}).size()",
                    10 => $"{E("any Sized")}.twice()",
                    11 => $"{E("Int[>..]")}.count",
                    12 => Chance(6) ? $"{E("Int[>..]")}[{Next(3)}]" : $"firstOr({E("Int[>..]")}, {E("Int")})",
                    13 => $"{E("(Int, String)")}.0",
                    14 => $"{E("Int[>..]")}.reduce(0, (a, b) => a &+ b)",
                    _ => $"({E("I32")} as Int)",
                };
            case "Bool":
                return Next(8) switch {
                    0 => $"({E("Int")} {Pick("<", "<=", ">", ">=", "==", "!=")} {E("Int")})",
                    1 => $"({E("Bool")} {Pick("&&", "||")} {E("Bool")})",
                    2 => $"!({E("Bool")})",
                    3 => $"({E("String")} {Pick("==", "<")} {E("String")})",
                    4 => $"({E("Pair")} == {E("Pair")})",
                    5 => $"({E("Int?")} == none)",
                    6 => $"({E("F64")} < {E("F64")})",
                    _ => $"{E("Int[>..]")}.isEmpty",
                };
            case "String":
                return Next(6) switch {
                    0 => $"({E("String")} + {E("String")})",
                    1 => $"\"<\\({E("Int").Replace("\"", "\\\"", StringComparison.Ordinal)})>\"" is var quoted && !quoted.Contains("\\\"", StringComparison.Ordinal) ? quoted : E("String"),
                    2 => $"shown({E("Bool")})",
                    3 => $"{E("Pair")}.b",
                    4 => $"{E("(Int, String)")}.1",
                    _ => $"shown({E("F64")})",
                };
            case "I32":
                return Next(3) switch {
                    0 => $"({E("I32")} {Pick("&+", "&*", "&-")} {E("I32")})",
                    1 => $"I32.truncating({E("Int")})",
                    _ => $"({E("U8")} as I32)",
                };
            case "U8":
                return Next(2) switch {
                    0 => $"({E("U8")} {Pick("&+", "^", "&*")} {E("U8")})",
                    _ => $"U8.truncating({E("Int")})",
                };
            case "F64":
                return Next(3) switch {
                    0 => $"({E("F64")} {Pick("+", "-", "*")} {E("F64")})",
                    1 => $"F64({E("I32")})",
                    _ => $"-({E("F64")})",
                };
            case "Int[>..]":
                return Next(5) switch {
                    0 => $"list([{E("Int")}, {E("Int")}])",
                    1 => $"with({E("Int[>..]")}, {E("Int")})",
                    2 => $"{E("Int[>..]")}.map((x) => x &* {Next(9)})",
                    3 => $"{E("Int[>..]")}.filter((x) => x > {E("Int")})",
                    _ => $"{E("Int[>..]")}.sorted()",
                };
            case "Int?":
                return Next(3) switch {
                    0 => $"maybe({E("Int")})",
                    1 => $"{E("Int[>..]")}.first",
                    _ => $"{E("Box")}.next?.value",
                };
            case "Pair":
                return Next(2) switch {
                    0 => $"Pair {{ a: {E("Int")}, b: {E("String")} }}",
                    _ => $"largest({E("Pair")}, {E("Pair")})",
                };
            case "Tree":
                return Next(2) switch {
                    0 => $"Tree.Value({E("Int")})",
                    _ => $"Tree.Branch([{E("Tree")}, {E("Tree")}], {E("String")})",
                };
            case "Box":
                return Next(2) switch {
                    0 => $"Box({E("Int")})",
                    _ => $"box(Wide(wide: {E("Int")}, {E("Int")}))",
                };
            case "any Sized":
                return $"sized({E(Pick("Pair", "Box"))})";
            case "(Int, String)":
                return Chance(2) ? $"swapped({E("String")}, {E("Int")})" : $"({E("Int")}, {E("String")})";
            case "Stack<Int>":
                return $"{E(Pick("Stack<Int>", "Stack<String>"))}.mapped((x) => {Next(9)})";
            case "Stack<String>":
                return $"{E("Stack<Int>")}.mapped((x) => \"{Next(9)}\")";
            default:
                return Simple(type);
        }
    }
}
