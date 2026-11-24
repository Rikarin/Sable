using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
///     A derived <c>Serializable</c> conformance's two members, as bound trees:
///     <c>encode(to:)</c>, which writes the value to an Encoder through each
///     field's own <c>encode</c>, and <c>static decode(from:)</c>, which reads it
///     back through each field type's own <c>decode</c> — calls to the
///     requirement, found for each field's type when SIL lowering knows it, as
///     a derived hash finds each field's <c>hash(into:)</c>.
/// </summary>
/// <remarks>
///     A struct is an object of its fields, under their keys (<see cref="SerializeOptions" />);
///     an enum, its case's name. A union's case is tagged as the type asks: by
///     default as <c>{"Case": payload}</c>, a case that carries nothing as its
///     bare name; untagged, it is the payload alone, or null, read back as the
///     first case it decodes as. Nothing here knows the format: the members are
///     generic over it, and call the requirements of Encoder and Decoder. A
///     payload of one value without a label is that value; one of
///     labelled values, an object; one of several values without labels, an
///     array.
/// </remarks>
internal sealed partial class DerivedBodies {
    NamedTypeSymbol Encoder => compilation.SerializeType("Encoder")!;
    NamedTypeSymbol Decoder => compilation.SerializeType("Decoder")!;

    FunctionSymbol SerializeRequirement(string name) =>
        compilation.SerializeType("Serializable")!.GetMembers(name).OfType<FunctionSymbol>().First();

    static FunctionSymbol MemberOf(NamedTypeSymbol type, string name) =>
        type.GetMembers(name).OfType<FunctionSymbol>().First();

    /// <summary>The member's own 'E: Encoder' or 'D: Decoder': the format, whatever it is.</summary>
    TypeParameterSymbol Format => _function.TypeParameters[0];

    BoundExpression EncoderParameter() => new BoundParameterAccess(_at, _function.Parameters[0], Format);
    BoundExpression DecoderParameter() => new BoundParameterAccess(_at, _function.Parameters[0], Format);

    /// <summary>A call to a requirement of Encoder's or Decoder's, on a value of the format's type, which is its Self.</summary>
    BoundExpression Call(BoundExpression receiver, NamedTypeSymbol protocol, string name, params BoundExpression[] arguments) {
        var function = MemberOf(protocol, name);
        var substitution = new TypeSubstitution(new() { [protocol.SelfTypeParameter!] = receiver.Type });
        return new BoundCall(_at, receiver, function, [..arguments], substitution.Apply(function.ReturnType)) { Substitution = substitution };
    }

    /// <summary>A call to Serializable's generic requirement, for the format this member is called with.</summary>
    BoundExpression CallRequirement(BoundExpression receiver, string name, BoundExpression argument, TypeSymbol result) {
        var requirement = SerializeRequirement(name);
        var substitution = new TypeSubstitution(new() {
                [compilation.SerializeType("Serializable")!.SelfTypeParameter!] = receiver.Type,
                [requirement.TypeParameters[0]] = Format,
            }
        );
        return new BoundCall(_at, receiver, requirement, [argument], result) { Substitution = substitution };
    }

    BoundExpression Property(BoundExpression receiver, string name) =>
        new BoundFieldAccess(_at, receiver, Decoder.GetMembers(name).OfType<FieldSymbol>().First(), core.Bool);

    BoundExpression Try(BoundExpression expression) => new BoundTry(_at, expression.Type, expression, TryKind.Propagate, null);

    BoundStatement Statement(BoundExpression expression) => new BoundExpressionStatement(_at, expression);

    // ---- Writing -------------------------------------------------------------

    BoundStatement Write(string encoderMember, params BoundExpression[] arguments) {
        var call = Call(EncoderParameter(), Encoder, encoderMember, arguments);
        return Statement(MemberOf(Encoder, encoderMember).ThrownType is null ? call : Try(call));
    }

    /// <summary><c>try value.encode(to: encoder)</c>, through the requirement.</summary>
    BoundStatement EncodeValue(BoundExpression value) =>
        Statement(Try(CallRequirement(value, "encode", EncoderParameter(), core.Void)));

    /// <summary>How many values follow, as 'Int?': none when that is known only as they are written.</summary>
    BoundExpression Count(int? count) {
        var number = new OptionalTypeSymbol(core.Int);
        return count is { } known
            ? new BoundConversion(_at, number, Literal((long)known), ConversionKind.OptionalWrap)
            : new BoundLiteral(_at, number, null);
    }

    BoundStatement BeginStruct(string name, int? count) => Write("beginStruct", Literal(name), Count(count));

    List<BoundStatement>? Encode(NamedTypeSymbol type, NamedTypeSymbol self) {
        var options = SerializeOptions.Of(type);
        if (type.TypeKind == TypeKind.Enum) {
            var arms = type.Members.OfType<EnumCaseSymbol>()
                .Select(c => new BoundMatchArm(
                        _at,
                        [new BoundEnumCasePattern(_at, self, c)],
                        null,
                        new BoundBlock(_at, [], [Write("encodeUnitCase", Literal(options.MemberOf(c, c.Name).Key), Literal(type.Name))])
                    )
                )
                .ToImmutableArray();
            return [Statement(new BoundMatch(_at, core.Void, new BoundSelfAccess(_at, self), arms))];
        }

        if (type.TypeKind == TypeKind.Union) {
            var arms = new List<BoundMatchArm>();
            foreach (var unionCase in Cases(type)) {
                var (pattern, locals) = Bind(self, unionCase, "a");
                var name = options.MemberOf(unionCase, unionCase.Name).Key;
                var body = new List<BoundStatement>();
                if (options.Untagged) {
                    body.AddRange(unionCase.HasPayload ? EncodePayload(unionCase, locals) : [Write("encodeNull")]);
                } else if (options.Tag is { } tag) {
                    var beside = options.Content is null ? locals.Length : unionCase.HasPayload ? 1 : 0;
                    body.Add(BeginStruct(type.Name, 1 + beside));
                    body.Add(Write("encodeField", Literal(tag)));
                    body.Add(Write("encodeString", Literal(name)));
                    if (options.Content is not { } content) {
                        body.AddRange(EncodeLabelled(unionCase, locals));
                    } else if (unionCase.HasPayload) {
                        body.Add(Write("encodeField", Literal(content)));
                        body.AddRange(EncodePayload(unionCase, locals));
                    }

                    body.Add(Write("endStruct"));
                } else if (!unionCase.HasPayload) {
                    body.Add(Write("encodeUnitCase", Literal(name), Literal(type.Name)));
                } else {
                    body.Add(Write("beginCase", Literal(name), Literal(type.Name)));
                    body.AddRange(EncodePayload(unionCase, locals));
                    body.Add(Write("endCase"));
                }

                arms.Add(new BoundMatchArm(_at, [pattern], null, new BoundBlock(_at, [], [..body])));
            }

            return [Statement(new BoundMatch(_at, core.Void, new BoundSelfAccess(_at, self), [..arms]))];
        }

        var fields = DerivedConformance.StoredFields(type)
            .Select(field => (Field: field, Member: options.MemberOf(field, field.Name)))
            .Where(static f => !f.Member.Skip)
            .ToList();
        var statements = new List<BoundStatement> { BeginStruct(type.Name, fields.Any(static f => f.Member.Flatten) ? null : fields.Count) };
        foreach (var (field, member) in fields) {
            statements.Add(member.Flatten ? Write("flattenNext") : Write("encodeField", Literal(member.Key)));
            statements.Add(EncodeValue(Field(new BoundSelfAccess(_at, self), field)));
        }

        statements.Add(Write("endStruct"));
        return statements;
    }

    IEnumerable<BoundStatement> EncodeLabelled(UnionCaseSymbol unionCase, ImmutableArray<LocalSymbol> locals) {
        for (var k = 0; k < locals.Length; k++) {
            yield return Write("encodeField", Literal(unionCase.Payload[k].Label!));
            yield return EncodeValue(Local(locals[k]));
        }
    }

    /// <summary>A case's payload: its one value, a struct of its labelled values, or a sequence of several.</summary>
    List<BoundStatement> EncodePayload(UnionCaseSymbol unionCase, ImmutableArray<LocalSymbol> locals) {
        if (locals.Length == 1 && unionCase.Payload[0].Label is null) {
            return [EncodeValue(Local(locals[0]))];
        }

        return unionCase.Payload.All(static p => p.Label is not null)
            ? [BeginStruct(unionCase.Name, locals.Length), ..EncodeLabelled(unionCase, locals), Write("endStruct")]
            : [Write("beginSequence", Count(locals.Length)), ..locals.Select(l => EncodeValue(Local(l))), Write("endSequence")];
    }

    // ---- Reading -------------------------------------------------------------

    /// <summary><c>try T.decode(from: decoder)</c>, through the requirement.</summary>
    BoundExpression DecodeValue(TypeSymbol type, BoundExpression decoder) =>
        Try(CallRequirement(new BoundTypeExpression(_at, type), "decode", decoder, type));

    BoundExpression FieldOf(BoundExpression decoder, string key) => Call(decoder, Decoder, "field", Literal(key));

    List<BoundStatement>? Decode(NamedTypeSymbol type, NamedTypeSymbol self) {
        var options = SerializeOptions.Of(type);
        var substitution = TypeSubstitution.For(self);
        BoundExpression CaseName() => Try(Call(DecoderParameter(), Decoder, "caseName"));
        BoundExpression CasePayload() => Call(DecoderParameter(), Decoder, "casePayload");
        if (type.TypeKind == TypeKind.Enum) {
            var arms = type.Members.OfType<EnumCaseSymbol>()
                .Select(c => new BoundMatchArm(
                        _at,
                        [new BoundLiteralPattern(_at, core.String, options.MemberOf(c, c.Name).Key, false)],
                        null,
                        new BoundBlock(_at, [], [new BoundReturn(_at, new BoundLiteral(_at, self, c.Name))])
                    )
                )
                .Append(new BoundMatchArm(_at, [], null, new BoundBlock(_at, [], [])))
                .ToImmutableArray();
            return [
                // A case with a payload is no enum's.
                new BoundIf(
                    _at,
                    Property(CasePayload(), "isMissing"),
                    Statement(new BoundMatch(_at, core.Void, CaseName(), arms)),
                    new BoundThrow(_at, Call(DecoderParameter(), Decoder, "mismatch", Literal("a case's name")))
                ),
                new BoundThrow(_at, Call(DecoderParameter(), Decoder, "unknownCase", CaseName())),
            ];
        }

        if (type.TypeKind == TypeKind.Union && options.Untagged) {
            return DecodeUntagged(type, self, substitution);
        }

        if (type.TypeKind == TypeKind.Union) {
            var statements = new List<BoundStatement>();
            BoundExpression Tag() => options.Tag is { } tag
                ? Try(Call(FieldOf(DecoderParameter(), tag), Decoder, "decodeString"))
                : CaseName();
            var beside = options.Tag is not null && options.Content is null;
            if (options.Tag is not null) {
                statements.Add(Statement(Try(Call(DecoderParameter(), Decoder, "beginStruct", Literal(type.Name)))));
            }

            var arms = new List<BoundMatchArm>();
            foreach (var unionCase in Cases(type)) {
                var payload = beside ? DecoderParameter()
                    : options.Content is { } content ? FieldOf(DecoderParameter(), content)
                    : CasePayload();
                arms.Add(new BoundMatchArm(
                        _at,
                        [new BoundLiteralPattern(_at, core.String, options.MemberOf(unionCase, unionCase.Name).Key, false)],
                        null,
                        DecodeCase(self, unionCase, payload, beside, substitution)
                    )
                );
            }

            arms.Add(new BoundMatchArm(_at, [], null, new BoundBlock(_at, [], [])));
            statements.Add(Statement(new BoundMatch(_at, core.Void, Tag(), [..arms])));
            statements.Add(new BoundThrow(_at, Call(DecoderParameter(), Decoder, "unknownCase", Tag())));
            return statements;
        }

        var fields = new List<(FieldSymbol, BoundExpression)>();
        foreach (var field in DerivedConformance.StoredFields(type)) {
            var fieldType = substitution.Apply(field.Type);
            var member = options.MemberOf(field, field.Name);
            var initial = member.Skip || member.Default ? Initial(field, fieldType) : null;
            if ((member.Skip || member.Default) && initial is null) {
                return null;
            }

            // What is flattened sees what is there but the keys the rest of
            // the struct takes: a map has what is left over.
            if (member.Flatten) {
                var taken = SerializeOptions.Keys(type, substitution, field).Select(Literal).ToImmutableArray();
                var keys = new BoundArray(_at, new ArrayTypeSymbol(core.String, ArrayKind.Grow), taken);
                fields.Add((field, DecodeValue(fieldType, Call(DecoderParameter(), Decoder, "excluding", keys))));
                continue;
            }

            var read = DecodeValue(fieldType, FieldOf(DecoderParameter(), member.Key));
            var value = member.Skip ? initial!
                : member.Default ? new BoundConditional(_at, fieldType, Call(DecoderParameter(), Decoder, "hasField", Literal(member.Key)), read, initial!)
                : read;
            fields.Add((field, value));
        }

        return [
            Statement(Try(Call(DecoderParameter(), Decoder, "beginStruct", Literal(type.Name)))),
            new BoundReturn(_at, new BoundRecordExpression(_at, self, [..fields])),
        ];
    }

    /// <summary>
    ///     A case read from where its payload is, and returned: its one value,
    ///     a struct's fields — already begun, when they are beside the tag —
    ///     or a sequence's elements.
    /// </summary>
    BoundBlock DecodeCase(NamedTypeSymbol self, UnionCaseSymbol unionCase, BoundExpression payload, bool beside, TypeSubstitution substitution) {
        if (!unionCase.HasPayload) {
            return new BoundBlock(_at, [], [new BoundReturn(_at, new BoundUnionCaseConstruction(_at, self, unionCase, []))]);
        }

        var from = new LocalSymbol("$payload", _function, null, false) { TypeResolver = () => payload.Type };
        var statements = new List<BoundStatement> { new BoundLocalDeclaration(_at, from, payload) };
        var labelled = unionCase.Payload.All(static p => p.Label is not null);
        var single = unionCase.Payload.Length == 1 && !labelled;
        if (labelled && !beside) {
            statements.Add(Statement(Try(Call(Local(from), Decoder, "beginStruct", Literal(unionCase.Name)))));
        }

        var arguments = unionCase.Payload.Select((part, k) => DecodeValue(
                substitution.Apply(part.Type),
                single ? Local(from) : labelled ? FieldOf(Local(from), part.Label!) : Call(Local(from), Decoder, "element", Literal((long)k))
            )
        );
        statements.Add(new BoundReturn(_at, new BoundUnionCaseConstruction(_at, self, unionCase, [..arguments])));
        return new BoundBlock(_at, [from], [..statements]);
    }

    /// <summary>
    ///     An untagged union: each case in order, the first whose payload
    ///     decodes — one that carries nothing, from null.
    /// </summary>
    List<BoundStatement> DecodeUntagged(NamedTypeSymbol type, NamedTypeSymbol self, TypeSubstitution substitution) {
        var error = compilation.SerializeType("SerializeError")!;
        var statements = new List<BoundStatement>();
        foreach (var unionCase in Cases(type)) {
            var body = DecodeCase(self, unionCase, DecoderParameter(), false, substitution);
            if (!unionCase.HasPayload) {
                statements.Add(new BoundIf(_at, Property(DecoderParameter(), "isNull"), body, null));
                continue;
            }

            var caught = new LocalSymbol("$error", _function, null, false) { TypeResolver = () => error };
            statements.Add(new BoundDo(_at, body, [new BoundCatch(_at, null, null, new BoundBlock(_at, [], []), caught)]));
        }

        statements.Add(new BoundThrow(_at, Call(DecoderParameter(), Decoder, "mismatch", Literal($"a case of {type.Name}"))));
        return statements;
    }

    /// <summary>A field's initial value, as its declaration gives it; an optional's, none.</summary>
    BoundExpression? Initial(FieldSymbol field, TypeSymbol type) =>
        field.HasInitializer ? compilation.BindFieldDefault(field, type)
        : type is OptionalTypeSymbol ? new BoundLiteral(_at, type, null)
        : null;
}
