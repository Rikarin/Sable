using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     C interop (SYNTAX.md §24): <c>@extern("c", "name")</c> declares a C
///     function a program calls, <c>@export("c", "name")</c> gives a Sable
///     function a C name to be called by.
/// </summary>
/// <remarks>
///     <para>
///         <c>@extern("c")</c> alone takes the C name to be the function's;
///         <c>@export("name")</c> alone takes the language to be C. Either is
///         a function of the module, not generic, not async and not
///         throwing: C has none of those. An <c>@extern</c> function has no
///         body (the C library's is its body); an <c>@export</c> one has.
///     </para>
///     <para>
///         What crosses must be something C has the same way: the integer
///         types, <c>F32</c> and <c>F64</c>, <c>Bool</c>, a <c>RawPtr</c>,
///         and a struct of those, which is passed as C passes the struct of
///         the same fields (LlvmEmitter's C calling convention). A string, a class, an
///         optional or a closure is not, and is S3224.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    /// <summary>Reads the function's <c>@extern</c> and <c>@export</c>, reporting what is wrong with them (S3223).</summary>
    void DeclareForeign(FunctionSymbol function, FunctionDeclarationSyntax declaration, Symbol container) {
        foreach (var attribute in declaration.Attributes) {
            var name = attribute.Name.ToString().Trim();
            if (name is not ("extern" or "export")) {
                continue;
            }

            var exporting = name == "export";
            if (ForeignNameOf(attribute, exporting, function.Name) is not { } foreignName) {
                continue;
            }

            string? wrong = null;
            if (exporting ? function.ExportName is not null : function.ForeignName is not null) {
                wrong = $"a function has one '@{name}'";
            } else if (container is not ModuleSymbol) {
                wrong = $"an '@{name}' function is one of the module's: C has no methods";
            } else if (declaration.TypeParameters is not null) {
                wrong = $"an '@{name}' function is not generic: C has one function for one signature";
            } else if (function.IsAsync || declaration.ThrowsSpec is not null) {
                wrong = $"an '@{name}' function neither waits nor throws: C does neither";
            } else if (!exporting && (declaration.Body is not null || declaration.ExpressionBody is not null)) {
                wrong = "an '@extern' function has no body: the C library's is its body";
            } else if (exporting && declaration.Body is null && declaration.ExpressionBody is null) {
                wrong = "an '@export' function has a body: it is what C calls";
            } else if (exporting ? function.ForeignName is not null : function.ExportName is not null) {
                wrong = "a function is '@extern' or '@export', not both";
            }

            if (wrong is not null) {
                ReportDeclaration(ErrorCode.ERR_MalformedForeign, attribute.Span, wrong);
                continue;
            }

            if (exporting) {
                function.ExportName = foreignName;
            } else {
                function.ForeignName = foreignName;
            }
        }
    }

    /// <summary>
    ///     Reads a variable's <c>@export</c>: a variable of the module, stored
    ///     rather than computed, whose storage C reads and writes by the name
    ///     (S3223). Its type is checked with the body (<see cref="CheckForeignVariable" />).
    /// </summary>
    void DeclareExportedVariable(FieldSymbol field, FieldDeclarationSyntax declaration, Symbol container) {
        foreach (var attribute in declaration.Attributes) {
            if (attribute.Name.ToString().Trim() != "export" || ForeignNameOf(attribute, exporting: true, field.Name) is not { } foreignName) {
                continue;
            }

            var wrong = field.ExportName is not null ? "a variable has one '@export'"
                : container is not ModuleSymbol || field.IsStatic ? "an '@export' variable is one of the module's: C has no types with members"
                : field.IsComputed || field.IsLazy ? "an '@export' variable is stored: C reads and writes its memory"
                : null;
            if (wrong is not null) {
                ReportDeclaration(ErrorCode.ERR_MalformedForeign, attribute.Span, wrong);
                continue;
            }

            field.ExportName = foreignName;
        }
    }

    /// <summary>An <c>@export</c> variable whose type C does not have (S3224).</summary>
    internal IEnumerable<Diagnostic> CheckForeignVariable(FieldSymbol field, FieldDeclarationSyntax declaration) {
        if (field.ExportName is not null && WhyNotC(field.Type, []) is { } why) {
            yield return Diagnostic.Error(ErrorCode.ERR_NotACType, declaration.Declarator.Span, $"'{field.Name}' cannot be seen by C: {why}");
        }
    }

    /// <summary>
    ///     The C name an <c>@extern</c> or <c>@export</c> gives — written, or
    ///     else the function's own — or null, having reported why not.
    /// </summary>
    string? ForeignNameOf(AttributeSyntax attribute, bool exporting, string functionName) {
        var arguments = attribute.ArgumentList?.Arguments.Select(static a => a.NameToken.IsNone
            && a.Expression is LiteralExpressionSyntax { Token: { Kind: SyntaxKind.StringLiteralToken, Value: string text } } ? text : null).ToList() ?? [];

        string? language = null;
        string? name = null;
        switch (arguments) {
            case [{ } only] when exporting && only != "c":
                name = only;
                break;
            case [{ } only]:
                language = only;
                break;
            case [{ } first, { } second]:
                language = first;
                name = second;
                break;
            default:
                ReportDeclaration(ErrorCode.ERR_MalformedForeign, attribute.Span, exporting
                    ? "'@export' takes the C name, and the language before it if written, as string literals: '@export(\"c\", \"sable_entry\")'"
                    : "'@extern' takes the language, and the C name after it if it is not the function's, as string literals: '@extern(\"c\", \"abs\")'");
                return null;
        }

        if (language is not null && language != "c") {
            ReportDeclaration(ErrorCode.ERR_MalformedForeign, attribute.Span,
                $"Sable calls and is called by C alone: \"{language}\" is not a language it knows (SYNTAX.md §24)");
            return null;
        }

        name ??= functionName;
        if (!IsCIdentifier(name)) {
            ReportDeclaration(ErrorCode.ERR_MalformedForeign, attribute.Span, $"'{name}' is not a name C can give a function or a variable");
            return null;
        }

        return name;
    }

    static bool IsCIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    ///     Reports each parameter or result of an <c>@extern</c> or
    ///     <c>@export</c> function that C does not have (S3224).
    /// </summary>
    internal IEnumerable<Diagnostic> CheckForeignSignature(FunctionSymbol function, FunctionDeclarationSyntax declaration) {
        if (function.ForeignName is null && function.ExportName is null) {
            yield break;
        }

        var attribute = function.ForeignName is not null ? "@extern" : "@export";

        // What C does with a pointer, nothing here can check: who calls it vouches.
        if (function.ForeignName is not null && !function.IsUnsafe && function.Parameters.FirstOrDefault(static p => HasCPointer(p.Type)) is { } pointer) {
            yield return Diagnostic.Error(ErrorCode.ERR_ForeignPointerNotUnsafe, declaration.Identifier.Span,
                $"'{function.Name}' gives C the pointer '{pointer.Name}', and what C does with it only the caller can vouch for: declare it 'unsafe fn'");
        }

        foreach (var (parameter, syntax) in function.Parameters.Zip(declaration.Parameters.Parameters)) {
            // 'args: CVarArg...': C's '...', after at least one fixed parameter.
            if (parameter.IsVariadic) {
                var why = function.ForeignName is null ? "C calls an '@export' function with its parameters fixed"
                    : !IsCVarArgs(parameter.Type) ? "the variadic arguments C takes are 'CVarArg...'"
                    : ReferenceEquals(parameter, function.Parameters[0]) ? "C's '...' follows at least one fixed parameter"
                    : null;
                if (why is not null) {
                    yield return Diagnostic.Error(ErrorCode.ERR_NotACType, syntax.Span, $"'{parameter.Name}' cannot cross to C: {why}");
                }

                continue;
            }

            if (parameter.Modifier != ParameterModifier.None) {
                yield return Diagnostic.Error(ErrorCode.ERR_NotACType, syntax.Span,
                    $"an '{attribute}' function's parameter is passed by value, as C passes it; '{parameter.Name}' is not");
            } else if ((WhyNotC(parameter.Type, []) ?? WhyNotWasm(parameter.Type)) is { } why) {
                yield return Diagnostic.Error(ErrorCode.ERR_NotACType, syntax.Span,
                    $"'{parameter.Name}' cannot cross to C: {why}");
            }
        }

        if (!ReferenceEquals(function.ReturnType, Core.Void) && (WhyNotC(function.ReturnType, []) ?? WhyNotWasm(function.ReturnType)) is { } result) {
            yield return Diagnostic.Error(ErrorCode.ERR_NotACType,
                declaration.ReturnClause?.Span ?? declaration.Identifier.Span, $"the result cannot cross to C: {result}");
        }
    }

    /// <summary>A variadic parameter's type that is Sable.Core's 'CVarArg...'.</summary>
    bool IsCVarArgs(TypeSymbol type) =>
        type is ArrayTypeSymbol { ElementType: NamedTypeSymbol { TypeKind: TypeKind.Protocol, Name: "CVarArg" } protocol }
        && ReferenceEquals(protocol.ContainingSymbol, Core.Module);

    /// <summary>
    ///     On WebAssembly C's addresses are 32 bits and Sable's 64: a pointer
    ///     is passed as C's, but a struct holding one is not laid out as C's.
    /// </summary>
    string? WhyNotWasm(TypeSymbol type) =>
        SyntaxTrees is [{ Options.TargetArch: "wasm32" }, ..] && !IsCPointer(type) && HasCPointer(type)
            ? $"'{type.ToDisplayString()}' holds an address, which is 64 bits and C's on WebAssembly 32; pass a pointer to C's own struct"
            : null;

    /// <summary>
    ///     A C pointer: Sable.Core's <c>RawPtr</c>, or a struct whose one
    ///     field is one (<c>NonNull</c>, Sable.Interop's <c>CFunction</c>, a
    ///     program's own handle), generic or not: only what it points at is.
    /// </summary>
    static bool IsCPointer(TypeSymbol type, int depth = 0) {
        if (type is not NamedTypeSymbol { TypeKind: TypeKind.Struct } structure || depth > 32) {
            return false;
        }

        var definition = structure.OriginalDefinition;
        if (definition is { Name: "RawPtr", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }) {
            return true;
        }

        return StoredFields(definition).Take(2).ToList() is [var only] && !HasDeinit(definition) && IsCPointer(only.Type, depth + 1);
    }

    /// <summary>Whether the type is a C pointer, or a struct with one somewhere in it.</summary>
    static bool HasCPointer(TypeSymbol type, int depth = 0) =>
        IsCPointer(type) || depth < 32 && type is NamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericDefinition: false } structure
        && StoredFields(structure).Any(f => HasCPointer(f.Type, depth + 1));

    static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol structure) =>
        structure.Members.OfType<FieldSymbol>().Where(static f => !f.IsStatic && !f.IsComputed);

    static bool HasDeinit(NamedTypeSymbol structure) =>
        structure.Members.OfType<FunctionSymbol>().Any(static f => f.FunctionKind == FunctionKind.Deinitializer);

    /// <summary>True for Sable.Interop's <c>CSignature</c>: what a function type C can call, or be called by, meets.</summary>
    internal static bool IsCSignatureProtocol(TypeSymbol type) =>
        type is NamedTypeSymbol { Name: "CSignature", TypeKind: TypeKind.Protocol, ContainingSymbol: ModuleSymbol { Name: "Sable.Interop" } };

    /// <summary>
    ///     Why C has no function of the type, or null when it has:
    ///     <c>CSignature</c>, which function types alone meet, by what they
    ///     take and give.
    /// </summary>
    internal static string? WhyNotCSignature(TypeSymbol type) {
        if (type is not FunctionTypeSymbol function) {
            return $"'{type.ToDisplayString()}' is not a function type";
        }

        if (function.IsAsync || function.ThrownType is not null || function.IsRethrows) {
            return "a C function neither waits nor throws";
        }

        if (!function.InOutParameters.IsEmpty) {
            return "C passes by value, never 'inout'";
        }

        foreach (var parameter in function.ParameterTypes) {
            if (WhyNotC(parameter, []) is { } why) {
                return why;
            }
        }

        return function.ReturnType is NamedTypeSymbol { Name: "Void", TypeKind: TypeKind.Builtin } ? null : WhyNotC(function.ReturnType, []);
    }

    /// <summary>Why C has no value of the type the same way, or null when it has.</summary>
    static string? WhyNotC(TypeSymbol type, HashSet<TypeSymbol> visiting) {
        if (type.IsError || IsCPointer(type)) {
            return null;
        }

        if (type is NamedTypeSymbol { TypeKind: TypeKind.Builtin } primitive
            && primitive.Name is "Int" or "UInt" or "I8" or "I16" or "I32" or "I64" or "U8" or "U16" or "U32" or "U64" or "F32" or "F64" or "Bool") {
            return null;
        }

        // A struct of what C has is the C struct of the same fields, in order.
        if (type is NamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericDefinition: false } structure && visiting.Add(structure)) {
            if (structure.IsConstructed) {
                return $"'{structure.ToDisplayString()}' is generic, and C has no generics";
            }

            // A deinit runs where Sable lets go of the value; C would copy it and never run it.
            if (HasDeinit(structure)) {
                return $"'{structure.Name}' has a deinit, which C would never run";
            }

            var fields = StoredFields(structure).ToList();
            if (fields.Count == 0) {
                return $"'{structure.Name}' has no fields, and a C struct has at least one";
            }

            foreach (var field in fields) {
                if (WhyNotC(field.Type, visiting) is { } why) {
                    return $"'{structure.Name}.{field.Name}' is '{field.Type.ToDisplayString()}', and {why}";
                }
            }

            visiting.Remove(structure);
            return null;
        }

        return $"'{type.ToDisplayString()}' is not a C type; what crosses is an integer, F32, F64, Bool, a RawPtr, or a struct of those";
    }
}
