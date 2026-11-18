using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sable.CodeGen;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>A C header that could not be read: clang's errors, or no clang.</summary>
public sealed class BindgenException(string message) : Exception(message);

/// <summary>
///     <c>sable bindgen</c> (docs/05 §5.1): Sable declarations for what a C
///     header declares — <c>@extern("c")</c> functions, structs, and the
///     integer constants of its enums and <c>#define</c>s.
/// </summary>
/// <remarks>
///     <para>
///         The header is read by clang itself (<c>-ast-dump=json</c>), so what
///         is bound is what C sees after the preprocessor and every typedef,
///         not a guess from the text. Only the header's own declarations are
///         bound, not those of the headers it includes.
///     </para>
///     <para>
///         What crosses to C is what the compiler can pass (SYNTAX.md §24):
///         integers, <c>F32</c>, <c>F64</c>, <c>Bool</c>, and structs of
///         those, by value. A function taking or giving anything else — a
///         pointer, an array, a union, a function pointer — or taking variable
///         arguments is left out with a comment saying why, until
///         <c>Sable.Interop</c> has pointers and C strings (#74). An enum is
///         its constants and <c>I32</c>, which is how C passes it.
///     </para>
///     <para>
///         C's <c>long</c> has the platform's width — 64 bits on macOS and
///         Linux, 32 on Windows — and <c>char</c> its signedness. The
///         fixed-width types of <c>stdint.h</c>, and <c>size_t</c>, are bound
///         by their names, not by what the platform's headers make them: an
///         <c>int64_t</c> is <c>I64</c> on Linux, where it is a <c>long</c>,
///         as on macOS, where it is a <c>long long</c>.
///     </para>
/// </remarks>
public static partial class Bindgen {
    /// <summary>The Sable source binding <paramref name="header" />, as module <paramref name="module" />.</summary>
    /// <param name="clangArguments">More for clang: include directories (<c>-I</c>), definitions (<c>-D</c>).</param>
    public static string Generate(string header, string module, IReadOnlyList<string> clangArguments) {
        if (!File.Exists(header)) {
            throw new BindgenException($"no such file '{header}'");
        }

        var start = new ProcessStartInfo(NativeBuild.CompilerPath) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in (string[])["-Xclang", "-ast-dump=json", "-fsyntax-only", "-x", "c", ..clangArguments, header]) {
            start.ArgumentList.Add(argument);
        }

        NativeBuild.LogCommand(start.FileName, [..start.ArgumentList]);
        string json;
        try {
            using var process = Process.Start(start) ?? throw new BindgenException($"could not run '{NativeBuild.CompilerPath}'");
            var errors = process.StandardError.ReadToEndAsync();
            json = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) {
                throw new BindgenException($"clang could not read '{header}':\n{errors.Result.TrimEnd()}");
            }
        } catch (System.ComponentModel.Win32Exception) {
            throw new BindgenException($"cannot run '{NativeBuild.CompilerPath}', which reads the header; set CC to a clang");
        }

        return FromAst(JsonNode.Parse(json, documentOptions: new() { MaxDepth = 4096 })!.AsObject(), File.ReadAllText(header), Path.GetFileName(header), module);
    }

    /// <summary>The module name a header's file name suggests: <c>my_lib.h</c> is <c>MyLib</c>.</summary>
    public static string ModuleNameFor(string header) {
        var builder = new StringBuilder();
        foreach (var part in Path.GetFileNameWithoutExtension(header).Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)) {
            builder.Append(char.ToUpperInvariant(part[0])).Append(part[1..]);
        }

        var name = builder.ToString();
        return name.Length == 0 || !char.IsAsciiLetter(name[0]) ? "C" + name : name;
    }

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)[ \t]+\(?(-?(?:0[xX][0-9A-Fa-f]+|\d+))[uUlL]*\)?[ \t]*(?://.*|/\*.*\*/)?$", RegexOptions.Multiline)]
    private static partial Regex IntegerMacro();

    /// <summary>A C type as Sable has it, or why it cannot cross.</summary>
    readonly record struct Mapped(string? Sable, string? Reason) {
        public static Mapped Of(string sable) => new(sable, null);
        public static Mapped No(string reason) => new(null, reason);
    }

    sealed class Context {
        /// <summary>Every typedef clang saw, the header's and its includes': what each name stands for.</summary>
        public readonly Dictionary<string, JsonObject> Typedefs = new(StringComparer.Ordinal);

        /// <summary>The header's structs, by clang's id, with their Sable names.</summary>
        public readonly Dictionary<string, string> Structs = new(StringComparer.Ordinal);

        /// <summary>A struct's name by its C spelling: <c>struct Size</c>, and the typedef naming it.</summary>
        public readonly Dictionary<string, string> StructByName = new(StringComparer.Ordinal);

        public readonly HashSet<string> Enums = new(StringComparer.Ordinal);
        public readonly HashSet<string> EnumIds = new(StringComparer.Ordinal);
    }

    static string FromAst(JsonObject root, string headerText, string headerName, string module) {
        var context = new Context();
        var all = root["inner"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        foreach (var node in all.Where(static n => Kind(n) == "TypedefDecl" && n["name"] is not null)) {
            context.Typedefs[node["name"]!.GetValue<string>()] = node;
        }

        // The header's own declarations: clang marks those of an included
        // file with where it was included from.
        var own = all.Where(static n => n["loc"]?["includedFrom"] is null && n["loc"]?["expansionLoc"]?["includedFrom"] is null && n["isImplicit"] is null).ToList();

        var output = new StringBuilder();
        output.Append(CultureInfo.InvariantCulture, $"// Generated by `sable bindgen` from {headerName}: what C declares there that Sable can call.\n");
        output.Append(OperatingSystem.IsWindows()
            ? "// C's 'long' is 32 bits here, as on Windows. Regenerate rather than edit.\n"
            : "// C's 'long' is 64 bits here, as on macOS and Linux. Regenerate rather than edit.\n");
        output.Append(CultureInfo.InvariantCulture, $"module {module}\n");

        // Structs first, naming the anonymous ones after the typedef that owns them.
        for (var i = 0; i < own.Count; i++) {
            var node = own[i];
            if (Kind(node) != "RecordDecl" || node["tagUsed"]?.GetValue<string>() != "struct" || node["completeDefinition"] is null) {
                continue;
            }

            var id = node["id"]!.GetValue<string>();
            var name = node["name"]?.GetValue<string>() is { Length: > 0 } named ? named
                : own.Skip(i + 1).FirstOrDefault(t => Kind(t) == "TypedefDecl" && t["inner"]?[0]?["ownedTagDecl"]?["id"]?.GetValue<string>() == id)?["name"]?.GetValue<string>();
            if (name is null) {
                continue;
            }

            context.Structs[id] = Identifier(name);
            context.StructByName["struct " + name] = Identifier(name);
        }

        foreach (var node in own.Where(static n => Kind(n) == "EnumDecl")) {
            context.EnumIds.Add(node["id"]!.GetValue<string>());
            if (node["name"]?.GetValue<string>() is { Length: > 0 } name) {
                context.Enums.Add("enum " + name);
            }
        }

        var body = new StringBuilder();
        var skipped = new List<string>();
        var bound = new HashSet<string>(StringComparer.Ordinal);

        // A struct is bound when each field is something C passes as Sable
        // does; one that is not is left out, and so is everything using it.
        var structs = own.Where(n => Kind(n) == "RecordDecl" && n["id"] is { } id && context.Structs.ContainsKey(id.GetValue<string>())).ToList();
        var unbindable = new Dictionary<string, string>(StringComparer.Ordinal);
        bool changed;
        do {
            changed = false;
            foreach (var node in structs) {
                var id = node["id"]!.GetValue<string>();
                if (unbindable.ContainsKey(id)) {
                    continue;
                }

                foreach (var field in node["inner"]?.AsArray().OfType<JsonObject>().Where(static f => Kind(f) == "FieldDecl") ?? []) {
                    var mapped = Map(field["type"]!.AsObject(), context, unbindable);
                    if (mapped.Sable is null) {
                        unbindable[id] = $"its field '{field["name"]?.GetValue<string>()}' is {mapped.Reason}";
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        foreach (var node in structs) {
            var id = node["id"]!.GetValue<string>();
            var name = context.Structs[id];
            if (unbindable.TryGetValue(id, out var reason)) {
                skipped.Add($"struct {name}: {reason}");
                continue;
            }

            body.Append(CultureInfo.InvariantCulture, $"\npublic struct {name} {{\n");
            foreach (var field in node["inner"]?.AsArray().OfType<JsonObject>().Where(static f => Kind(f) == "FieldDecl") ?? []) {
                var fieldName = field["name"]?.GetValue<string>() is { Length: > 0 } f ? Identifier(f) : "_";
                body.Append(CultureInfo.InvariantCulture, $"    public var {fieldName}: {Map(field["type"]!.AsObject(), context, unbindable).Sable}\n");
            }

            body.Append("}\n");
        }

        // The constants: each enum's, then the header's integer #defines.
        var constants = new StringBuilder();
        foreach (var node in own.Where(static n => Kind(n) == "EnumDecl")) {
            long next = 0;
            foreach (var constant in node["inner"]?.AsArray().OfType<JsonObject>().Where(static c => Kind(c) == "EnumConstantDecl") ?? []) {
                var value = constant["inner"]?.AsArray().OfType<JsonObject>().Select(static v => v["value"]?.GetValue<string>()).FirstOrDefault(static v => v is not null) is { } text
                    ? long.Parse(text, CultureInfo.InvariantCulture)
                    : next;
                next = value + 1;
                var name = constant["name"]!.GetValue<string>();
                if (bound.Add(name)) {
                    constants.Append(CultureInfo.InvariantCulture, $"public let {Identifier(name)}: I32 = {value}\n");
                }
            }
        }

        foreach (Match macro in IntegerMacro().Matches(headerText)) {
            var name = macro.Groups[1].Value;
            var text = macro.Groups[2].Value;
            var negative = text.StartsWith('-');
            var digits = negative ? text[1..] : text;
            var parsed = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : (long?)null
                : long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var decimalValue) ? decimalValue : null;
            if (parsed is { } value && bound.Add(name)) {
                constants.Append(CultureInfo.InvariantCulture, $"public let {Identifier(name)}: Int = {(negative ? -value : value)}\n");
            }
        }

        if (constants.Length > 0) {
            body.Append('\n').Append(constants);
        }

        foreach (var node in own.Where(static n => Kind(n) == "FunctionDecl")) {
            var name = node["name"]!.GetValue<string>();
            if (node["storageClass"]?.GetValue<string>() == "static" || !bound.Add(name)) {
                // A static function has no symbol to link to; a second declaration is the first.
                continue;
            }

            if (node["variadic"] is not null) {
                skipped.Add($"fn {name}: it takes variable arguments (...), which Sable does not pass");
                continue;
            }

            var parameters = node["inner"]?.AsArray().OfType<JsonObject>().Where(static p => Kind(p) == "ParmVarDecl").ToList() ?? [];
            var declaration = new StringBuilder();
            string? why = null;
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var p = 0; p < parameters.Count && why is null; p++) {
                var mapped = Map(parameters[p]["type"]!.AsObject(), context, unbindable);
                if (mapped.Sable is null) {
                    why = $"its parameter {(parameters[p]["name"]?.GetValue<string>() is { } n ? $"'{n}'" : (p + 1).ToString(CultureInfo.InvariantCulture))} is {mapped.Reason}";
                    break;
                }

                var parameter = parameters[p]["name"]?.GetValue<string>() is { Length: > 0 } given ? Identifier(given) : $"p{p}";
                while (!used.Add(parameter)) {
                    parameter += "_";
                }

                declaration.Append(p == 0 ? "" : ", ").Append(CultureInfo.InvariantCulture, $"_ {parameter}: {mapped.Sable}");
            }

            var returned = ReturnType(node["type"]!["qualType"]!.GetValue<string>());
            string? result = null;
            if (why is null && returned is not null && returned != "void") {
                var mapped = Map(new JsonObject { ["qualType"] = returned }, context, unbindable);
                if (mapped.Sable is null) {
                    why = $"it returns {mapped.Reason}";
                } else {
                    result = mapped.Sable;
                }
            } else if (returned is null) {
                why = "it returns a function";
            }

            if (why is not null) {
                skipped.Add($"fn {name}: {why}");
                continue;
            }

            var sableName = Identifier(name);
            body.Append('\n');
            body.Append(sableName == name ? "@extern(\"c\")\n" : $"@extern(\"c\", \"{name}\")\n");
            body.Append(CultureInfo.InvariantCulture, $"public fn {sableName}({declaration}){(result is null ? "" : $" -> {result}")}\n");
        }

        output.Append(body);
        if (skipped.Count > 0) {
            output.Append("\n// Not bound: pointers, arrays, unions and C strings wait for Sable.Interop (#74).\n");
            foreach (var line in skipped) {
                output.Append("//   ").Append(line).Append('\n');
            }
        }

        return output.ToString();
    }

    static string? Kind(JsonObject node) => node["kind"]?.GetValue<string>();

    /// <summary>A function type's result, from clang's spelling of it (<c>int (int, int)</c>); null for one returning a function.</summary>
    static string? ReturnType(string functionType) {
        var depth = 0;
        for (var i = functionType.Length - 1; i >= 0; i--) {
            depth += functionType[i] switch { ')' => 1, '(' => -1, _ => 0 };
            if (depth == 0) {
                var result = functionType[..i].Trim();
                return result.Contains('(') ? null : result;
            }
        }

        return null;
    }

    /// <summary>What a C type is in Sable, through every typedef.</summary>
    static Mapped Map(JsonObject type, Context context, IReadOnlyDictionary<string, string> unbindable, int depth = 0) {
        var spelled = Strip(type["qualType"]!.GetValue<string>());
        return MapSpelling(spelled, context, unbindable, depth);
    }

    static Mapped MapSpelling(string spelled, Context context, IReadOnlyDictionary<string, string> unbindable, int depth) {
        if (depth > 32) {
            return Mapped.No("a typedef that never ends");
        }

        if (spelled.Contains('*')) {
            return Mapped.No($"a pointer ({spelled})");
        }

        if (spelled.Contains('[')) {
            return Mapped.No($"an array ({spelled})");
        }

        if (spelled.Contains('(')) {
            return Mapped.No($"a function ({spelled})");
        }

        if (FixedWidth(spelled) is { } fixedWidth) {
            return Mapped.Of(fixedWidth);
        }

        if (Builtin(spelled) is { } builtin) {
            return builtin.Length == 0 ? Mapped.No($"C's '{spelled}', which Sable has no type for") : Mapped.Of(builtin);
        }

        if (context.StructByName.TryGetValue(spelled, out var structName)) {
            var id = context.Structs.First(s => s.Value == structName).Key;
            return unbindable.ContainsKey(id) ? Mapped.No($"struct {structName}, which is not bound") : Mapped.Of(structName);
        }

        if (spelled.StartsWith("enum ", StringComparison.Ordinal)) {
            return Mapped.Of("I32");
        }

        if (spelled.StartsWith("union ", StringComparison.Ordinal)) {
            return Mapped.No($"a union ({spelled})");
        }

        if (spelled.StartsWith("struct ", StringComparison.Ordinal)) {
            return Mapped.No($"{spelled}, which this header does not define");
        }

        if (context.Typedefs.TryGetValue(spelled, out var typedef)) {
            // A typedef of a struct or enum is that struct or enum, by clang's id.
            var inner = typedef["inner"]?[0];
            var declared = inner?["ownedTagDecl"]?["id"]?.GetValue<string>() ?? inner?["inner"]?[0]?["decl"]?["id"]?.GetValue<string>();
            if (declared is not null && context.Structs.TryGetValue(declared, out var named)) {
                return unbindable.ContainsKey(declared) ? Mapped.No($"struct {named}, which is not bound") : Mapped.Of(named);
            }

            if (declared is not null && context.EnumIds.Contains(declared)) {
                return Mapped.Of("I32");
            }

            var underlying = typedef["type"]!["desugaredQualType"]?.GetValue<string>() ?? typedef["type"]!["qualType"]!.GetValue<string>();
            return underlying == spelled ? Mapped.No($"'{spelled}', which this header does not define") : MapSpelling(Strip(underlying), context, unbindable, depth + 1);
        }

        return Mapped.No($"'{spelled}', which this header does not define");
    }

    /// <summary>A C type's spelling without the qualifiers that do not change how it is passed.</summary>
    static string Strip(string spelled) {
        foreach (var qualifier in (string[])["const ", "volatile ", "restrict ", "__restrict "]) {
            spelled = spelled.Replace(qualifier, "", StringComparison.Ordinal);
        }

        return spelled.Replace(" const", "", StringComparison.Ordinal).Trim();
    }

    /// <summary>The C library's types of a width every platform agrees on: their Sable type, or null.</summary>
    static string? FixedWidth(string spelled) => spelled switch {
        "int8_t" => "I8",
        "uint8_t" => "U8",
        "int16_t" => "I16",
        "uint16_t" => "U16",
        "int32_t" => "I32",
        "uint32_t" => "U32",
        "int64_t" => "I64",
        "uint64_t" => "U64",
        "size_t" or "uintptr_t" => "UInt",
        "ssize_t" or "ptrdiff_t" or "intptr_t" => "Int",
        _ => null,
    };

    /// <summary>C's own types: their Sable type, "" for one Sable lacks, null for one that is not built in.</summary>
    static string? Builtin(string spelled) => spelled switch {
        "void" => "void",
        "_Bool" or "bool" => "Bool",
        "char" => RuntimeInformation.OSArchitecture == Architecture.Arm64 && OperatingSystem.IsLinux() ? "U8" : "I8",
        "signed char" => "I8",
        "unsigned char" => "U8",
        "short" or "short int" or "signed short" or "signed short int" => "I16",
        "unsigned short" or "unsigned short int" => "U16",
        "int" or "signed" or "signed int" => "I32",
        "unsigned" or "unsigned int" => "U32",
        "long" or "long int" or "signed long" or "signed long int" => OperatingSystem.IsWindows() ? "I32" : "Int",
        "unsigned long" or "unsigned long int" => OperatingSystem.IsWindows() ? "U32" : "UInt",
        "long long" or "long long int" or "signed long long" or "signed long long int" => "I64",
        "unsigned long long" or "unsigned long long int" => "U64",
        "float" => "F32",
        "double" => "F64",
        "long double" or "__int128" or "unsigned __int128" or "_Float16" or "__fp16" => "",
        _ => null,
    };

    /// <summary>A C name as a Sable identifier: one that is a Sable keyword gets a <c>_</c> after it.</summary>
    static string Identifier(string name) =>
        SyntaxFacts.KeywordKind(name) != SyntaxKind.IdentifierToken || SyntaxFacts.IsFutureReserved(name) ? name + "_" : name;
}
