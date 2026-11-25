using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>What an attribute may be written on.</summary>
[Flags]
internal enum AttributeTargets {
    None = 0,
    Function = 1 << 0,
    Struct = 1 << 1,
    Class = 1 << 2,
    Actor = 1 << 3,
    Protocol = 1 << 4,
    Union = 1 << 5,
    Enum = 1 << 6,
    Extension = 1 << 7,
    Initializer = 1 << 8,
    Deinitializer = 1 << 9,
    Subscript = 1 << 10,
    Operator = 1 << 11,
    Field = 1 << 12,
    Accessor = 1 << 13,
    Const = 1 << 14,
    Alias = 1 << 15,
    AssociatedType = 1 << 16,
    UnionCase = 1 << 17,
    EnumCase = 1 << 18,
    Parameter = 1 << 19,
    GenericParameter = 1 << 20,
    Module = 1 << 21,

    Type = Struct | Class | Actor | Protocol | Union | Enum,
    Callable = Function | Initializer | Deinitializer | Subscript | Operator | Accessor,
    Any = ~None,
}

/// <summary>
///     The attributes spec/SYNTAX.md §22 lists, and the compiler's own: each
///     known by name, and allowed only where it means something. An attribute
///     nothing knows is an error, as one written in the wrong place is — an
///     attribute that is silently ignored is worse than none.
/// </summary>
public sealed partial class Compilation {
    /// <summary>Every attribute there is, with where it may go and how that place is described.</summary>
    static readonly Dictionary<string, (AttributeTargets Targets, string Where)> KnownAttributes = new(StringComparer.Ordinal) {
        ["inline"] = (AttributeTargets.Callable, "a function"),
        ["inlinable"] = (AttributeTargets.Callable, "a function"),
        ["vectorised"] = (AttributeTargets.Callable, "a function"),
        ["inline_layout"] = (AttributeTargets.Struct | AttributeTargets.Union, "a struct or a union"),
        ["noncopy"] = (AttributeTargets.Struct | AttributeTargets.Union, "a struct or a union"),
        ["reflectable"] = (AttributeTargets.Type, "a type"),
        ["immutable"] = (AttributeTargets.Class, "a class"),
        ["immortal"] = (AttributeTargets.Field, "a 'static let'"),
        ["flags"] = (AttributeTargets.Enum, "an enum"),
        ["escaping"] = (AttributeTargets.Parameter, "a closure parameter"),
        ["Sendable"] = (AttributeTargets.Parameter, "a closure parameter"),
        ["autoclosure"] = (AttributeTargets.Parameter, "a parameter"),
        ["polymorphic"] = (AttributeTargets.Callable, "a generic function"),
        ["deprecated"] = (AttributeTargets.Any & ~AttributeTargets.Module, "a declaration"),
        ["available"] = (AttributeTargets.Any & ~AttributeTargets.Module, "a declaration"),
        ["extern"] = (AttributeTargets.Function, "a function"),
        ["export"] = (AttributeTargets.Function | AttributeTargets.Field, "a function or a variable"),
        ["packed"] = (AttributeTargets.Struct, "a struct"),
        ["align"] = (AttributeTargets.Struct, "a struct"),
        ["test"] = (AttributeTargets.Function, "a function"),
        ["bench"] = (AttributeTargets.Function, "a function"),
        ["fuzz"] = (AttributeTargets.Function, "a function"),
        ["main"] = (AttributeTargets.Function, "a function"),
        ["discardable"] = (AttributeTargets.Callable, "a function"),
        ["MainActor"] = (AttributeTargets.Any & ~AttributeTargets.Module, "a declaration"),
        ["frozen"] = (AttributeTargets.Union, "a union"),
        ["derive"] = (AttributeTargets.Type, "a type"),
        ["serialize"] = (AttributeTargets.Type | AttributeTargets.Field | AttributeTargets.UnionCase | AttributeTargets.EnumCase, "a type, a property or a case"),
        ["indirect"] = (AttributeTargets.Union | AttributeTargets.UnionCase, "a union or a union case"),
        ["generator"] = (AttributeTargets.Function, "a function in a package's generators/ directory (docs/08)"),

        // The compiler's own: lint levels, and Sable.Core's ties to the
        // runtime, which are checked where they are read.
        ["allow"] = (AttributeTargets.Any, "a declaration"),
        ["warn"] = (AttributeTargets.Any, "a declaration"),
        ["builtin"] = (AttributeTargets.Any, "a declaration"),
        ["intrinsic"] = (AttributeTargets.Any, "a declaration"),
    };

    /// <summary>What a declaration is, as an attribute's place: null for a node no attribute is written on.</summary>
    static (AttributeTargets Target, string Noun)? AttributeTargetOf(SyntaxNode? node) =>
        node switch {
            FunctionDeclarationSyntax => (AttributeTargets.Function, "a function"),
            StructDeclarationSyntax => (AttributeTargets.Struct, "a struct"),
            ClassDeclarationSyntax => (AttributeTargets.Class, "a class"),
            ActorDeclarationSyntax => (AttributeTargets.Actor, "an actor"),
            ProtocolDeclarationSyntax => (AttributeTargets.Protocol, "a protocol"),
            UnionDeclarationSyntax => (AttributeTargets.Union, "a union"),
            EnumDeclarationSyntax => (AttributeTargets.Enum, "an enum"),
            ExtensionDeclarationSyntax => (AttributeTargets.Extension, "an extension"),
            InitDeclarationSyntax => (AttributeTargets.Initializer, "an initialiser"),
            DeinitDeclarationSyntax => (AttributeTargets.Deinitializer, "a deinitialiser"),
            SubscriptDeclarationSyntax => (AttributeTargets.Subscript, "a subscript"),
            OperatorDeclarationSyntax => (AttributeTargets.Operator, "an operator"),
            FieldDeclarationSyntax => (AttributeTargets.Field, "a property"),
            AccessorSyntax => (AttributeTargets.Accessor, "an accessor"),
            ConstDeclarationSyntax => (AttributeTargets.Const, "a constant"),
            AliasDeclarationSyntax => (AttributeTargets.Alias, "an alias"),
            AssociatedTypeDeclarationSyntax => (AttributeTargets.AssociatedType, "an associated type"),
            UnionCaseDeclarationSyntax => (AttributeTargets.UnionCase, "a union case"),
            EnumCaseDeclarationSyntax => (AttributeTargets.EnumCase, "an enum case"),
            ParameterSyntax => (AttributeTargets.Parameter, "a parameter"),
            GenericParameterSyntax => (AttributeTargets.GenericParameter, "a type parameter"),
            ModuleDeclarationSyntax => (AttributeTargets.Module, "a module declaration"),
            // 'f(_ k: @escaping () -> Int)': the parameter's, written on its type.
            AttributedTypeSyntax { Parent: ParameterSyntax } => (AttributeTargets.Parameter, "a parameter"),
            AttributedTypeSyntax => (AttributeTargets.None, "a type"),
            _ => null,
        };

    /// <summary>
    ///     The attributes a declaration may have more than one of: each names
    ///     lints or protocols of its own. (A second <c>@builtin</c>,
    ///     <c>@extern</c> or <c>@export</c> is reported where it is read.)
    /// </summary>
    static readonly HashSet<string> RepeatableAttributes = ["allow", "warn", "derive", "builtin", "extern", "export"];

    /// <summary>An attribute's name as written: <c>inline</c> for <c>@inline(always)</c>.</summary>
    internal static string AttributeName(AttributeSyntax attribute) => attribute.Name.ToString().Trim();

    /// <summary>The attributes written on a declaration.</summary>
    internal static IEnumerable<AttributeSyntax> AttributesOf(SyntaxNode declaration) =>
        declaration.ChildNodes().OfType<AttributeSyntax>();

    /// <summary>Checks every attribute in a file: that it is one there is, written where it applies, and with the arguments it takes.</summary>
    void CheckAttributes(SyntaxTree tree) {
        foreach (var attribute in tree.Root.DescendantNodes().OfType<AttributeSyntax>()) {
            var name = AttributeName(attribute);
            if (name.Length == 0) {
                continue;
            }

            if (!KnownAttributes.TryGetValue(name, out var known)) {
                if (IsGeneratorAttribute(tree, attribute, name, out var problem)) {
                    if (problem is not null) {
                        ReportDeclaration(ErrorCode.ERR_AttributeDoesNotApply, attribute.Span, problem);
                    }

                    continue;
                }

                // A struct conforming to Attribute, once every type is
                // declared and its conformances known.
                var unknown = attribute;
                TypeBinder.DeferLate(() => CheckUserAttribute(tree, unknown, name));
                continue;
            }

            if (AttributeTargetOf(attribute.Parent) is not var (target, noun)) {
                continue;
            }

            // '@immortal' is for a 'static let' alone.
            var applies = (known.Targets & target) != 0
                && !(name == "immortal" && attribute.Parent is FieldDeclarationSyntax field
                    && (field.LetOrVarKeyword.Kind != SyntaxKind.LetKeyword || !field.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword)));

            if (!applies) {
                ReportDeclaration(
                    ErrorCode.ERR_AttributeDoesNotApply,
                    attribute.Span,
                    $"'@{name}' applies to {known.Where}, not to {noun}"
                );
                continue;
            }

            // Said once is said: a second would contradict the first, or repeat it.
            if (!RepeatableAttributes.Contains(name) && SiblingAttributes(attribute).TakeWhile(a => a != attribute).Any(a => AttributeName(a) == name)) {
                ReportDeclaration(ErrorCode.ERR_DuplicateAttribute, attribute.Span, $"'@{name}' is written twice on one declaration");
                continue;
            }

            // A Sable.Core '@intrinsic' has the body each use is lowered to;
            // anywhere else there is none, and the declaration would be empty.
            if (name == "intrinsic" && CoreLibrary.ModuleOf(tree) != CoreLibrary.ModuleName) {
                ReportDeclaration(
                    ErrorCode.ERR_BuiltinOutsideCore,
                    attribute.Span,
                    "only Sable.Core may declare an '@intrinsic': its body is the compiler's, for each type it is used with"
                );
                continue;
            }

            CheckAttributeArguments(attribute, name);

            if (name is "escaping" or "Sendable" && attribute.Parent is ParameterSyntax or AttributedTypeSyntax { Parent: ParameterSyntax }) {
                var escaping = attribute;
                TypeBinder.DeferLate(() => CheckEscaping(escaping, name));
            }

            if (name == "autoclosure" && attribute.Parent is ParameterSyntax or AttributedTypeSyntax { Parent: ParameterSyntax }) {
                var autoclosure = attribute;
                TypeBinder.DeferLate(() => CheckAutoclosure(autoclosure));
            }

            if (name == "main" && attribute.Parent is FunctionDeclarationSyntax mainFunction) {
                var main = attribute;
                TypeBinder.DeferLate(() => CheckMain(mainFunction, main));
            }

            if (name == "immutable" && attribute.Parent is ClassDeclarationSyntax immutableClass) {
                TypeBinder.DeferLate(() => CheckImmutable(immutableClass));
            }

            if (name == "polymorphic" && attribute.Parent is FunctionDeclarationSyntax polymorphicFunction) {
                TypeBinder.DeferLate(() => CheckPolymorphic(polymorphicFunction, attribute));
            }
        }
    }

    /// <summary>The attributes written beside one, on the same declaration (or type), itself among them.</summary>
    static IEnumerable<AttributeSyntax> SiblingAttributes(AttributeSyntax attribute) =>
        attribute.Parent?.ChildNodes().OfType<AttributeSyntax>() ?? [];

    /// <summary>
    ///     <c>@escaping</c> says a closure parameter may outlive the call
    ///     (SYNTAX.md §22), and <c>@Sendable</c> that a closure passed to it
    ///     runs alongside the caller, in a task of its own (SYNTAX.md §19); on
    ///     a parameter of any other type either says nothing, so it is not
    ///     written there.
    /// </summary>
    void CheckEscaping(AttributeSyntax attribute, string name) {
        if (AttributedParameter(attribute) is not { Type: var type } parameter || type.IsError) {
            return;
        }

        var held = type is OptionalTypeSymbol optional ? optional.UnderlyingType : type;
        if (held is not FunctionTypeSymbol) {
            ReportDeclaration(
                ErrorCode.ERR_AttributeDoesNotApply,
                attribute.Span,
                $"'@{name}' applies to a closure parameter, and '{parameter.Name}' is of type '{type.ToDisplayString()}'"
            );
        }
    }

    /// <summary>
    ///     The names a function with an <c>@autoclosure</c> parameter is
    ///     called by: its own, or an initialiser's type's and <c>init</c>.
    ///     A call by one of them binds its arguments as a question first
    ///     (<see cref="Binder" />), as which are closures depends on the
    ///     overload; any other call binds them once.
    /// </summary>
    HashSet<string> AutoclosureCallees => _autoclosureCallees ??= FindAutoclosureCallees();

    HashSet<string>? _autoclosureCallees;

    internal bool MayTakeAutoclosure(string name) => AutoclosureCallees.Contains(name);

    HashSet<string> FindAutoclosureCallees() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in SyntaxTrees) {
            if (!tree.Text.Text.Contains("autoclosure", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var parameter in tree.Root.DescendantNodes().OfType<ParameterSyntax>()) {
                if (!parameter.Attributes.Concat((parameter.Type as AttributedTypeSyntax)?.Attributes ?? []).Any(static a => AttributeName(a) == "autoclosure")) {
                    continue;
                }

                for (var owner = parameter.Parent; owner is not null; owner = owner.Parent) {
                    if (owner is FunctionDeclarationSyntax function) {
                        names.Add(function.Identifier.Text);
                        break;
                    }

                    if (owner is InitDeclarationSyntax) {
                        names.Add("init");
                        for (var type = owner.Parent; type is not null; type = type.Parent) {
                            if (TypeName(type) is { } typeName) {
                                names.Add(typeName);
                                break;
                            }
                        }

                        break;
                    }
                }
            }
        }

        return names;

        static string? TypeName(SyntaxNode node) =>
            node switch {
                StructDeclarationSyntax s => s.Identifier.Text,
                ClassDeclarationSyntax c => c.Identifier.Text,
                ActorDeclarationSyntax a => a.Identifier.Text,
                UnionDeclarationSyntax u => u.Identifier.Text,
                EnumDeclarationSyntax e => e.Identifier.Text,
                ExtensionDeclarationSyntax x => x.Type.ToString().Trim(),
                _ => null,
            };
    }

    /// <summary>The parameter an attribute is written on, or on the type of.</summary>
    ParameterSymbol? AttributedParameter(AttributeSyntax attribute) {
        var syntax = attribute.Parent as ParameterSyntax ?? (ParameterSyntax)attribute.Parent!.Parent!;
        var owner = syntax.Parent;
        while (owner is not null && GetDeclaredSymbol(owner) is null) {
            owner = owner.Parent;
        }

        var parameters = owner is not null && GetDeclaredSymbol(owner) is FunctionSymbol function ? function.Parameters : [];
        return parameters.FirstOrDefault(p => p.DeclaringSyntax.Contains(syntax));
    }

    /// <summary>
    ///     <c>@autoclosure</c> makes the argument written at a call the body
    ///     of a closure taking nothing (SYNTAX.md §5): the parameter is a
    ///     <c>() -&gt; T</c>, and the argument a <c>T</c>. One argument is
    ///     one closure, so not a variadic parameter's; and a closure made
    ///     at the call is a value, not the caller's storage, so not
    ///     <c>inout</c>, <c>out</c> or <c>consuming</c> (S3515).
    /// </summary>
    void CheckAutoclosure(AttributeSyntax attribute) {
        if (AttributedParameter(attribute) is not { Type: var type } parameter || type.IsError) {
            return;
        }

        void Refuse(string why) =>
            ReportDeclaration(ErrorCode.ERR_InvalidAutoclosure, attribute.Span, $"'@autoclosure' wraps the argument in a closure that takes nothing, so {why}");

        if (parameter.IsVariadic) {
            Refuse($"'{parameter.Name}' cannot be variadic: each argument would be a closure of its own");
        } else if (type is not FunctionTypeSymbol function) {
            Refuse($"'{parameter.Name}' is of type '() -> {type.ToDisplayString()}', not '{type.ToDisplayString()}'");
        } else if (!function.ParameterTypes.IsEmpty) {
            Refuse($"'{parameter.Name}' takes nothing, and '{type.ToDisplayString()}' takes {(function.ParameterTypes.Length == 1 ? "a parameter" : "parameters")}");
        } else if (function.IsAsync) {
            Refuse($"'{parameter.Name}' cannot be 'async': the callee works the argument out where it asks for it, without waiting");
        } else if (parameter.Modifier is not ParameterModifier.None) {
            Refuse($"'{parameter.Name}' is the closure made at the call, not the caller's storage, and cannot be '{parameter.Modifier.ToString().ToLowerInvariant()}'");
        }
    }

    /// <summary>The <c>@main</c> of each module, the first written: a module starts in one place.</summary>
    readonly Dictionary<string, FunctionDeclarationSyntax> _mains = new(StringComparer.Ordinal);

    /// <summary>
    ///     <c>@main</c> marks where a program starts: a function of the
    ///     module, called with nothing, whose result — none, or an
    ///     <c>Int</c> — is the process's exit status. One per module.
    /// </summary>
    void CheckMain(FunctionDeclarationSyntax declaration, AttributeSyntax attribute) {
        void Refuse(string why) =>
            ReportDeclaration(ErrorCode.ERR_InvalidEntryPoint, attribute.Span, $"'@main' marks where the program starts, so {why}");

        if (declaration.Parent is not CompilationUnitSyntax) {
            Refuse($"'{declaration.Identifier.Text}' is a function of the module, not a method or a local function");
            return;
        }

        if (GetDeclaredSymbol(declaration) is not FunctionSymbol function) {
            return;
        }

        if (!function.TypeParameters.IsEmpty) {
            Refuse($"'{function.Name}' is not generic: nothing would say what its type parameters are");
            return;
        }

        if (!function.Parameters.IsEmpty) {
            Refuse($"'{function.Name}' takes no parameters: the program's arguments are 'Environment.arguments()'");
            return;
        }

        if (!function.ReturnType.IsError && !function.ReturnType.Equals(Core.Void) && !function.ReturnType.Equals(Core.Int)) {
            Refuse($"'{function.Name}' returns nothing, or an 'Int' that is the exit status, not '{function.ReturnType.ToDisplayString()}'");
            return;
        }

        var module = function.ContainingSymbol?.Name ?? "";
        lock (_mains) {
            if (_mains.TryGetValue(module, out var first) && first != declaration) {
                Refuse($"a module has one, and '{first.Identifier.Text}' is it");
                return;
            }

            _mains[module] = declaration;
        }
    }

    readonly Dictionary<AttributeSyntax, (NamedTypeSymbol Type, BoundExpression Value)> _userAttributes = [];

    /// <summary>
    ///     A user-defined attribute (SYNTAX.md §22): a struct conforming to
    ///     Sable.Core's <c>Attribute</c>, named as the attribute is, whose
    ///     arguments are an initialiser's, and constants. Any other name is
    ///     no attribute (S3092).
    /// </summary>
    void CheckUserAttribute(SyntaxTree tree, AttributeSyntax attribute, string name) {
        var scope = _fileScopes[tree];
        var context = AttributeContext(attribute) ?? scope.Module;
        var found = attribute.Name is SimpleNameTypeSyntax { TypeArgumentList: null } simple
            ? TypeBinder.Lookup(simple.Identifier.Text, context, scope, simple.Span, report: false)
            : null;
        if (found is not NamedTypeSymbol { TypeKind: TypeKind.Struct } type || Core.Attribute is not { } protocol
            || !type.BaseTypes.Contains(protocol)) {
            ReportDeclaration(
                ErrorCode.ERR_UnknownAttribute,
                attribute.Name.Span,
                found is NamedTypeSymbol other && Core.Attribute is not null
                    ? $"'{other.ToDisplayString()}' is not an attribute: an attribute of the program's own is a struct conforming to 'Attribute'"
                    : $"there is no attribute '@{name}'"
            );
            return;
        }

        if (type.IsGenericDefinition) {
            ReportDeclaration(ErrorCode.ERR_MalformedAttribute, attribute.Span, $"'@{name}' is generic, and an attribute's type is not");
            return;
        }

        var binder = new Binder(this, scope, context);
        var value = binder.BindAttribute(attribute, type);
        foreach (var diagnostic in binder.Diagnostics) {
            _declarationDiagnostics.Add(diagnostic);
        }

        if (value.HasError) {
            return;
        }

        // What reflection gives back is made again each time it is asked
        // for, so it can be nothing but constants.
        if (FirstNonConstant(value is BoundCall call ? call.Arguments : []) is { } variable) {
            ReportDeclaration(
                ErrorCode.ERR_AttributeArgumentNotConstant,
                variable.Syntax.Span,
                $"an argument of '@{name}' is a constant — a literal, a case, or an array or tuple of them — and this is not"
            );
            return;
        }

        _userAttributes[attribute] = (type, value);
    }

    /// <summary>The declaration an attribute is written on, as a symbol, if it is one.</summary>
    Symbol? AttributeContext(AttributeSyntax attribute) {
        for (var node = attribute.Parent; node is not null; node = node.Parent) {
            if (GetDeclaredSymbol(node) is { } symbol) {
                return symbol is FunctionSymbol or NamedTypeSymbol ? symbol : symbol.ContainingSymbol;
            }
        }

        return null;
    }

    static BoundExpression? FirstNonConstant(IEnumerable<BoundExpression> arguments) {
        foreach (var argument in arguments) {
            var bad = argument switch {
                BoundLiteral => null,
                BoundMetatype => null,
                BoundConversion { ConversionKind: ConversionKind.Identity or ConversionKind.LiteralTyping or ConversionKind.OptionalWrap or ConversionKind.UnionInjection or ConversionKind.Existential } conversion => FirstNonConstant([conversion.Operand]),
                BoundUnaryOperator { Method: null } unary => FirstNonConstant([unary.Operand]),
                BoundBinaryOperator { Method: null } binary => FirstNonConstant([binary.Left, binary.Right]),
                BoundArray array => FirstNonConstant(array.Elements),
                BoundTuple tuple => FirstNonConstant(tuple.Elements),
                BoundUnionCaseConstruction construction => FirstNonConstant(construction.Arguments),
                _ => argument,
            };

            if (bad is not null) {
                return bad;
            }
        }

        return null;
    }

    /// <summary>
    ///     The program's own attributes written on a declaration, each its
    ///     struct and the value its arguments make, in the order written.
    /// </summary>
    public IReadOnlyList<(NamedTypeSymbol Type, BoundExpression Value)> UserAttributesOf(Symbol symbol) {
        var definition = symbol is NamedTypeSymbol named ? named.OriginalDefinition : symbol;
        if (definition.DeclaringSyntax is not [var declaration, ..]) {
            return [];
        }

        return [..AttributesOf(declaration).Where(_userAttributes.ContainsKey).Select(a => _userAttributes[a])];
    }

    /// <summary>
    ///     An attribute a source generator the file's package can see answers
    ///     to, or reads on members (docs/08), and if so, in
    ///     <paramref name="problem" />, why it does not apply where it is
    ///     written. A generator is given types, at the top level of a file or
    ///     in another type, functions at the top level, and methods of types and
    ///     extensions; and it reads attributes on their fields, cases and
    ///     parameters.
    /// </summary>
    static bool IsGeneratorAttribute(SyntaxTree tree, AttributeSyntax attribute, string name, out string? problem) {
        const AttributeTargets types = AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Actor | AttributeTargets.Union | AttributeTargets.Enum;
        const AttributeTargets members = AttributeTargets.Field | AttributeTargets.UnionCase | AttributeTargets.EnumCase | AttributeTargets.Parameter;
        var target = AttributeTargetOf(attribute.Parent)?.Target ?? AttributeTargets.None;
        var noun = AttributeTargetOf(attribute.Parent)?.Noun ?? "this";

        // Where the declaration is: at the top level, or in a type or an extension that is, in turn.
        static bool Placed(SyntaxNode? container) =>
            container is CompilationUnitSyntax
            || container is ExtensionDeclarationSyntax extension && extension.Parent is CompilationUnitSyntax
            || container is StructDeclarationSyntax or ClassDeclarationSyntax or ActorDeclarationSyntax or UnionDeclarationSyntax or EnumDeclarationSyntax
            && Placed(container.Parent);

        var onDeclaration = ((target & types) != 0 || target == AttributeTargets.Function) && Placed(attribute.Parent?.Parent);
        var onMember = (target & members) != 0;
        problem = null;
        if (tree.Options.GeneratorAttributes.Contains(name) && onDeclaration || tree.Options.GeneratorMemberAttributes.Contains(name) && onMember) {
            return true;
        }

        if (tree.Options.GeneratorAttributes.Contains(name)) {
            problem = $"'@{name}' starts a source generator, which is given a type, a function or a method, not {noun}";
            return true;
        }

        if (tree.Options.GeneratorMemberAttributes.Contains(name)) {
            problem = $"'@{name}' is read by a source generator on a field, a case or a parameter, not on {noun}";
            return true;
        }

        return false;
    }

    /// <summary>
    ///     An <c>@immutable</c> class is deeply immutable: every stored
    ///     property a <c>let</c>, and of a type that cannot change either — a
    ///     value, or another <c>@immutable</c> class.
    /// </summary>
    void CheckImmutable(ClassDeclarationSyntax declaration) {
        if (GetDeclaredSymbol(declaration) is not NamedTypeSymbol type) {
            return;
        }

        foreach (var field in type.Members.OfType<FieldSymbol>()) {
            if (field.IsStatic || field.IsComputed || field.DeclaringSyntax is not [var written, ..]) {
                continue;
            }

            if (field.IsMutable) {
                ReportDeclaration(
                    ErrorCode.ERR_NotDeeplyImmutable,
                    written.Span,
                    $"'{type.Name}' is '@immutable', so '{field.Name}' cannot be a 'var'; declare it with 'let'"
                );
                continue;
            }

            // Through an optional or an array, to the objects it holds.
            var fieldType = field.Type;
            while (fieldType is OptionalTypeSymbol or ArrayTypeSymbol) {
                fieldType = fieldType is OptionalTypeSymbol optional ? optional.UnderlyingType : ((ArrayTypeSymbol)fieldType).ElementType;
            }

            // An actor's state changes, but only on the actor, which every
            // task waits for: holding one is as safe as holding a value.
            if (fieldType is NamedTypeSymbol { TypeKind: TypeKind.Class } held
                && !(held.OriginalDefinition.DeclaringSyntax is [var heldDeclaration, ..]
                    && AttributesOf(heldDeclaration).Any(static a => AttributeName(a) == "immutable"))) {
                ReportDeclaration(
                    ErrorCode.ERR_NotDeeplyImmutable,
                    written.Span,
                    $"'{type.Name}' is '@immutable', so '{field.Name}' cannot hold '{held.ToDisplayString()}', "
                    + "an object that can change; make that class '@immutable' too"
                );
            }
        }
    }

    /// <summary>The most a struct may be aligned to: what every block the runtime allocates is aligned to.</summary>
    internal const int MaximumAlignment = 16;

    /// <summary>The alignment an '@align(n)' among a type's attributes gives it, when n is one it may have; otherwise 0.</summary>
    static int AlignmentOf(SyntaxList<AttributeSyntax> attributes) {
        foreach (var attribute in attributes) {
            if (AttributeName(attribute) == "align"
                && attribute.ArgumentList?.Arguments.ToList() is [{ Expression: LiteralExpressionSyntax { Token.Value: ulong alignment } }]
                && alignment is > 0 and <= MaximumAlignment && (alignment & (alignment - 1)) == 0) {
                return (int)alignment;
            }
        }

        return 0;
    }

    /// <summary>
    ///     A <c>@polymorphic</c> function is compiled once, each type
    ///     parameter standing for <c>any</c> of its constraints: a value of it
    ///     is passed boxed, with the witness table of its type's conformance.
    ///     That needs a signature a box can be passed through and taken back
    ///     out of, and constraints a box can meet (S3512).
    /// </summary>
    void CheckPolymorphic(FunctionDeclarationSyntax declaration, AttributeSyntax attribute) {
        if (GetDeclaredSymbol(declaration) is not FunctionSymbol function) {
            return;
        }

        void Refuse(string why) =>
            ReportDeclaration(
                ErrorCode.ERR_NotPolymorphic,
                attribute.Span,
                $"'@polymorphic' compiles '{function.Name}' once, each type parameter a box with its witness table, and {why}"
            );

        if (function.TypeParameters.IsEmpty) {
            Refuse("it has no type parameters");
            return;
        }

        for (Symbol? outer = function.ContainingSymbol; outer is not null; outer = outer.ContainingSymbol) {
            if (outer is FunctionSymbol) {
                Refuse("a local function is compiled with the one around it");
                return;
            }

            if (outer is NamedTypeSymbol { TypeParameters.IsEmpty: false } generic) {
                Refuse($"'{generic.Name}' around it is generic, and compiled for each type itself");
                return;
            }
        }

        bool MentionsAny(TypeSymbol type) => function.TypeParameters.Any(p => Mentions(type, p));

        foreach (var parameter in function.TypeParameters) {
            foreach (var constraint in parameter.Constraints) {
                if (constraint is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                    Refuse($"'{parameter.Name}: {constraint.ToDisplayString()}' is not a protocol, which a box can meet");
                    return;
                }

                if (SelfBound(protocol) is { } requirement) {
                    Refuse($"'{protocol.ToDisplayString()}' {requirement}, which a box of an unknown type cannot meet");
                    return;
                }
            }
        }

        foreach (var parameter in function.Parameters) {
            if (parameter.Type is TypeParameterSymbol && parameter.Modifier is not (ParameterModifier.None or ParameterModifier.In)) {
                Refuse($"'{parameter.Name}' is '{parameter.Modifier.ToString().ToLowerInvariant()}', and a box passed so could not be given back as the caller's value");
                return;
            }

            if (parameter.Type is not TypeParameterSymbol && MentionsAny(parameter.Type)) {
                Refuse($"'{parameter.Name}: {parameter.Type.ToDisplayString()}' holds a type parameter inside it, which would need each part boxed; write it as a type parameter, or one that mentions none");
                return;
            }
        }

        if (function.ReturnType is not TypeParameterSymbol && MentionsAny(function.ReturnType)) {
            Refuse($"its result '{function.ReturnType.ToDisplayString()}' holds a type parameter inside it; write it as a type parameter, or one that mentions none");
            return;
        }

        if (function.ThrownType is { } thrown && MentionsAny(thrown)) {
            Refuse($"what it throws, '{thrown.ToDisplayString()}', mentions a type parameter");
        }
    }

    /// <summary>
    ///     What of a protocol, or one it refines, a value in a box cannot do:
    ///     an associated type, or a requirement that mentions Self — or null.
    /// </summary>
    static string? SelfBound(NamedTypeSymbol protocol) {
        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<NamedTypeSymbol>([protocol.OriginalDefinition]);
        while (pending.TryPop(out var current)) {
            if (!seen.Add(current)) {
                continue;
            }

            if (!current.AssociatedTypes.IsEmpty) {
                return $"has an associated type, '{current.AssociatedTypes[0].Name}'";
            }

            if (current.SelfTypeParameter is { } self) {
                foreach (var function in current.Members.OfType<FunctionSymbol>()) {
                    if (function.Parameters.Any(p => Mentions(p.Type, self)) || Mentions(function.ReturnType, self)) {
                        return $"requires '{function.Name}', which mentions Self";
                    }
                }
            }

            foreach (var refined in current.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol)) {
                pending.Push(refined.OriginalDefinition);
            }
        }

        return null;
    }

    /// <summary>The platforms <c>@available</c> may name: what <c>#if os(...)</c> tests (ParseOptions.TargetOs).</summary>
    static readonly string[] Platforms = ["macos", "linux", "windows", "wasi"];

    /// <summary>The arguments of the attributes whose meaning depends on them, which are read later.</summary>
    void CheckAttributeArguments(AttributeSyntax attribute, string name) {
        var arguments = attribute.ArgumentList?.Arguments.ToList() ?? [];

        void Malformed(string message) =>
            ReportDeclaration(ErrorCode.ERR_MalformedAttribute, attribute.Span, message);

        static bool IsString(ExpressionSyntax e) =>
            e is LiteralExpressionSyntax { Token.Kind: SyntaxKind.StringLiteralToken or SyntaxKind.RawStringLiteralToken };

        switch (name) {
            case "deprecated" when arguments.Count > 1 || arguments is [var only] && (!IsString(only.Expression) || !only.NameToken.IsNone):
                Malformed("'@deprecated' takes at most a message, as a string: '@deprecated(\"use X\")'");
                return;

            // Platforms alone: a version would need a minimum one to build
            // for, to compare it with, and there is none.
            case "available" when arguments.Count == 0
                || arguments.Any(static a => !a.NameToken.IsNone || a.Expression is not IdentifierNameSyntax):
                Malformed($"'@available' names the platforms it is for, among {string.Join(", ", Platforms)}: '@available(macos, linux)'");
                return;

            case "available" when arguments.Select(static a => ((IdentifierNameSyntax)a.Expression).Identifier.Text).FirstOrDefault(p => !Platforms.Contains(p)) is { } unknown:
                Malformed($"'{unknown}' is no platform: '@available' names some of {string.Join(", ", Platforms)}");
                return;

            case "inline" when arguments is not [{ Expression: IdentifierNameSyntax { Identifier.Text: "always" or "never" } }]:
                Malformed("'@inline' takes 'always' or 'never': '@inline(always)'");
                return;

            case "align" when arguments is not [{ Expression: LiteralExpressionSyntax { Token.Value: ulong alignment } }] || alignment == 0 || (alignment & (alignment - 1)) != 0:
                Malformed("'@align' takes a power of two: '@align(16)'");
                return;

            // The runtime's blocks are aligned to 16 bytes, so a value in an
            // object or an array could be no more aligned than that.
            case "align" when arguments is [{ Expression: LiteralExpressionSyntax { Token.Value: ulong alignment } }] && alignment > MaximumAlignment:
                ReportDeclaration(
                    ErrorCode.ERR_AlignTooLarge,
                    attribute.Span,
                    $"'@align({alignment})' is more than {MaximumAlignment}: objects and arrays are aligned to {MaximumAlignment} bytes, so a value in one could not be aligned to {alignment}"
                );
                return;

            case "packed" or "discardable" or "frozen" or "immutable" or "immortal" or "polymorphic" or "noncopy" or "escaping" or "Sendable" or "flags" or "MainActor"
                or "reflectable" or "inline_layout" or "indirect" or "main" or "intrinsic" or "inlinable" or "vectorised"
                when attribute.ArgumentList is not null:
                Malformed($"'@{name}' takes no arguments");
                return;
        }
    }

    /// <summary>
    ///     A use of a declaration, as <c>@deprecated</c> and <c>@available</c>
    ///     have it: a deprecated one is warned of, unless the use is in it or
    ///     in something deprecated too; one available only on other platforms
    ///     than the one being built for is an error.
    /// </summary>
    internal void ReportUse(DiagnosticBag diagnostics, Symbol used, TextSpan span, Symbol? context, FileScope? scope) {
        var (deprecation, platforms) = AttributeFactsOf(used);
        if (deprecation is null && platforms is null) {
            return;
        }

        var definition = used is NamedTypeSymbol named ? named.OriginalDefinition : used;
        var inDeprecated = false;
        var availableHere = false;
        for (var here = context; here is not null; here = here.ContainingSymbol) {
            if (ReferenceEquals(here, definition)) {
                return;
            }

            // Code that is itself only for some of the platforms may use
            // what is available on all of those.
            var facts = AttributeFactsOf(here);
            inDeprecated |= facts.Deprecation is not null;
            availableHere |= platforms is { } needed && facts.Platforms is { } own && own.All(p => needed.Contains(p, StringComparer.Ordinal));
        }

        if (deprecation is not null && !inDeprecated) {
            diagnostics.Add(Diagnostic.Warning(
                ErrorCode.WRN_Deprecated,
                span,
                $"'{used.Name}' is deprecated{(deprecation.Length > 0 ? $": {deprecation}" : "")}"
            ));
        }

        var target = scope?.Tree.Options.TargetOs ?? Options.TargetOs;
        if (platforms is { } on && !on.Contains(target, StringComparer.Ordinal) && !availableHere) {
            diagnostics.Error(
                ErrorCode.ERR_NotAvailable,
                span,
                $"'{used.Name}' is available only on {string.Join(" and ", on)}, and this builds for {target}"
            );
        }
    }

    readonly Dictionary<Symbol, (string? Deprecation, ImmutableArray<string>? Platforms)> _attributeFacts = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     What <c>@deprecated</c> and <c>@available</c> say of a declaration:
    ///     the message it is deprecated with ("" for none), and the platforms
    ///     it is available on — null for each it does not say.
    /// </summary>
    internal (string? Deprecation, ImmutableArray<string>? Platforms) AttributeFactsOf(Symbol symbol) {
        var definition = symbol is NamedTypeSymbol named ? named.OriginalDefinition : symbol;
        lock (_attributeFacts) {
            if (_attributeFacts.TryGetValue(definition, out var known)) {
                return known;
            }
        }

        string? deprecation = null;
        ImmutableArray<string>? platforms = null;
        if (definition.DeclaringSyntax is [var declaration, ..]) {
            foreach (var attribute in AttributesOf(declaration)) {
                var arguments = attribute.ArgumentList?.Arguments.ToList() ?? [];
                switch (AttributeName(attribute)) {
                    case "deprecated":
                        deprecation = arguments is [{ Expression: LiteralExpressionSyntax { Token.Value: string message } }] ? message : "";
                        break;

                    case "available":
                        platforms = [
                            ..arguments.Select(static a => a.NameToken.IsNone
                                ? a.Expression is IdentifierNameSyntax platform ? platform.Identifier.Text : ""
                                : a.NameToken.Text)
                        ];
                        break;
                }
            }
        }

        lock (_attributeFacts) {
            _attributeFacts[definition] = (deprecation, platforms);
        }

        return (deprecation, platforms);
    }
}
