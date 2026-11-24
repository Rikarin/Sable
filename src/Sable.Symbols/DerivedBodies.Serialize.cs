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
///     bare name. A payload of one value without a label is that value; one of
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

    BoundExpression EncoderParameter() => new BoundParameterAccess(_at, _function.Parameters[0], Encoder);
    BoundExpression DecoderParameter() => new BoundParameterAccess(_at, _function.Parameters[0], Decoder);

    BoundExpression Call(BoundExpression receiver, NamedTypeSymbol type, string name, params BoundExpression[] arguments) {
        var function = MemberOf(type, name);
        return new BoundCall(_at, receiver, function, [..arguments], function.ReturnType);
    }

    BoundExpression Try(BoundExpression expression) => new BoundTry(_at, expression.Type, expression, TryKind.Propagate, null);

    BoundStatement Statement(BoundExpression expression) => new BoundExpressionStatement(_at, expression);

    // ---- Writing -------------------------------------------------------------

    BoundStatement Write(string encoderMember, params BoundExpression[] arguments) {
        var call = Call(EncoderParameter(), Encoder, encoderMember, arguments);
        return Statement(MemberOf(Encoder, encoderMember).ThrownType is null ? call : Try(call));
    }

    /// <summary><c>try value.encode(to: encoder)</c>, through the requirement.</summary>
    BoundStatement EncodeValue(BoundExpression value) =>
        Statement(Try(new BoundCall(_at, value, SerializeRequirement("encode"), [EncoderParameter()], core.Void)));

    List<BoundStatement>? Encode(NamedTypeSymbol type, NamedTypeSymbol self) {
        var options = SerializeOptions.Of(type);
        if (type.TypeKind == TypeKind.Enum) {
            var arms = type.Members.OfType<EnumCaseSymbol>()
                .Select(c => new BoundMatchArm(_at, [new BoundEnumCasePattern(_at, self, c)], null, new BoundBlock(_at, [], [Write("encodeString", Literal(options.MemberOf(c, c.Name).Key))])))
                .ToImmutableArray();
            return [Statement(new BoundMatch(_at, core.Void, new BoundSelfAccess(_at, self), arms))];
        }

        if (type.TypeKind == TypeKind.Union) {
            var arms = new List<BoundMatchArm>();
            foreach (var unionCase in Cases(type)) {
                var (pattern, locals) = Bind(self, unionCase, "a");
                var name = options.MemberOf(unionCase, unionCase.Name).Key;
                var body = new List<BoundStatement>();
                if (options.Tag is { } tag) {
                    body.Add(Write("beginObject"));
                    body.Add(Write("key", Literal(tag)));
                    body.Add(Write("encodeString", Literal(name)));
                    if (options.Content is { } content) {
                        if (unionCase.HasPayload) {
                            body.Add(Write("key", Literal(content)));
                            body.AddRange(EncodePayload(unionCase, locals));
                        }
                    } else {
                        for (var k = 0; k < locals.Length; k++) {
                            body.Add(Write("key", Literal(unionCase.Payload[k].Label!)));
                            body.Add(EncodeValue(Local(locals[k])));
                        }
                    }

                    body.Add(Write("endObject"));
                } else if (!unionCase.HasPayload) {
                    body.Add(Write("encodeString", Literal(name)));
                } else {
                    body.Add(Write("beginObject"));
                    body.Add(Write("key", Literal(name)));
                    body.AddRange(EncodePayload(unionCase, locals));
                    body.Add(Write("endObject"));
                }

                arms.Add(new BoundMatchArm(_at, [pattern], null, new BoundBlock(_at, [], [..body])));
            }

            return [Statement(new BoundMatch(_at, core.Void, new BoundSelfAccess(_at, self), [..arms]))];
        }

        var statements = new List<BoundStatement> { Write("beginObject") };
        foreach (var field in DerivedConformance.StoredFields(type)) {
            var member = options.MemberOf(field, field.Name);
            if (member.Skip) {
                continue;
            }

            statements.Add(Write("key", Literal(member.Key)));
            statements.Add(EncodeValue(Field(new BoundSelfAccess(_at, self), field)));
        }

        statements.Add(Write("endObject"));
        return statements;
    }

    /// <summary>A case's payload: its one value, an object of its labelled values, or an array of several.</summary>
    List<BoundStatement> EncodePayload(UnionCaseSymbol unionCase, ImmutableArray<LocalSymbol> locals) {
        if (locals.Length == 1 && unionCase.Payload[0].Label is null) {
            return [EncodeValue(Local(locals[0]))];
        }

        var labelled = unionCase.Payload.All(static p => p.Label is not null);
        var statements = new List<BoundStatement> { Write(labelled ? "beginObject" : "beginArray") };
        for (var k = 0; k < locals.Length; k++) {
            if (labelled) {
                statements.Add(Write("key", Literal(unionCase.Payload[k].Label!)));
            }

            statements.Add(EncodeValue(Local(locals[k])));
        }

        statements.Add(Write(labelled ? "endObject" : "endArray"));
        return statements;
    }

    // ---- Reading -------------------------------------------------------------

    /// <summary><c>try T.decode(from: decoder)</c>, through the requirement.</summary>
    BoundExpression DecodeValue(TypeSymbol type, BoundExpression decoder) =>
        Try(new BoundCall(_at, new BoundTypeExpression(_at, type), SerializeRequirement("decode"), [decoder], type));

    BoundExpression FieldOf(BoundExpression decoder, string key) => Call(decoder, Decoder, "field", Literal(key));

    List<BoundStatement>? Decode(NamedTypeSymbol type, NamedTypeSymbol self) {
        var options = SerializeOptions.Of(type);
        var substitution = TypeSubstitution.For(self);
        if (type.TypeKind == TypeKind.Enum) {
            var cases = type.Members.OfType<EnumCaseSymbol>().ToList();
            BoundExpression Name() => Try(Call(DecoderParameter(), Decoder, "decodeString"));
            var arms = cases
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
                Statement(new BoundMatch(_at, core.Void, Name(), arms)),
                new BoundThrow(_at, Call(DecoderParameter(), Decoder, "unknownCase", Name())),
            ];
        }

        if (type.TypeKind == TypeKind.Union) {
            var statements = new List<BoundStatement>();
            BoundExpression Tag() => options.Tag is { } tag
                ? Try(Call(FieldOf(DecoderParameter(), tag), Decoder, "decodeString"))
                : Try(Call(DecoderParameter(), Decoder, "variantTag"));
            BoundExpression Payload() => options.Tag is null
                ? Call(DecoderParameter(), Decoder, "variantPayload")
                : options.Content is { } content ? FieldOf(DecoderParameter(), content) : DecoderParameter();
            if (options.Tag is not null) {
                statements.Add(Statement(Try(Call(DecoderParameter(), Decoder, "expectObject", Literal(type.Name)))));
            }

            var arms = new List<BoundMatchArm>();
            foreach (var unionCase in Cases(type)) {
                var arguments = new List<BoundExpression>();
                var single = unionCase.Payload.Length == 1 && unionCase.Payload[0].Label is null && !(options.Tag is not null && options.Content is null);
                var labelled = unionCase.Payload.All(static p => p.Label is not null);
                for (var k = 0; k < unionCase.Payload.Length; k++) {
                    var payload = unionCase.Payload[k];
                    var payloadType = substitution.Apply(payload.Type);
                    var from = single ? Payload()
                        : labelled ? FieldOf(Payload(), payload.Label!)
                        : Call(Payload(), Decoder, "element", Literal((long)k));
                    arguments.Add(DecodeValue(payloadType, from));
                }

                var construction = new BoundUnionCaseConstruction(_at, self, unionCase, [..arguments]);
                arms.Add(new BoundMatchArm(
                        _at,
                        [new BoundLiteralPattern(_at, core.String, options.MemberOf(unionCase, unionCase.Name).Key, false)],
                        null,
                        new BoundBlock(_at, [], [new BoundReturn(_at, construction)])
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

            var read = DecodeValue(fieldType, FieldOf(DecoderParameter(), member.Key));
            var value = member.Skip ? initial!
                : member.Default ? new BoundConditional(_at, fieldType, Call(DecoderParameter(), Decoder, "hasField", Literal(member.Key)), read, initial!)
                : read;
            fields.Add((field, value));
        }

        return [
            Statement(Try(Call(DecoderParameter(), Decoder, "expectObject", Literal(type.Name)))),
            new BoundReturn(_at, new BoundRecordExpression(_at, self, [..fields])),
        ];
    }

    /// <summary>A field's initial value, as its declaration gives it; an optional's, none.</summary>
    BoundExpression? Initial(FieldSymbol field, TypeSymbol type) =>
        field.HasInitializer ? compilation.BindFieldDefault(field, type)
        : type is OptionalTypeSymbol ? new BoundLiteral(_at, type, null)
        : null;
}
