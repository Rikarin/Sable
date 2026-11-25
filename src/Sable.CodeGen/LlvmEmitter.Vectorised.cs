using System.Text;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     <c>@vectorised</c> functions (docs/04 §3.8): compiled once for the
///     target's baseline and, on x86-64, once more for a processor with AVX2,
///     with a function of the plain name that calls the one the processor the
///     program runs on can run. The two do exactly the same: what LLVM makes
///     of the same IR with more instructions to choose from, and nothing
///     fused or reassociated, as nothing says it may be.
/// </summary>
public sealed partial class LlvmEmitter {
    const string BaselineSuffix = "$baseline";
    const string WideSuffix = "$avx2";

    /// <summary>What the runtime's <c>PROCESSOR_AVX2</c> (processor.c) promises.</summary>
    internal const string WideFeatures = "+avx2,+bmi,+bmi2,+popcnt";

    internal const string WideAttributes = $"noinline \"target-features\"=\"{WideFeatures}\"";

    void Function(SilFunction function, StringBuilder output) {
        if (!function.Vectorised || !_target.Triple.StartsWith("x86_64", StringComparison.Ordinal) || PassesVectors(function)) {
            output.Append(new FunctionEmitter(this, function).Emit()).Append('\n');
            return;
        }

        output.Append(new FunctionEmitter(this, function, BaselineSuffix).Emit()).Append('\n');
        output.Append(new FunctionEmitter(this, function, WideSuffix).Emit()).Append('\n');
        var type = ReturnType(function.Type);
        var parameters = function.Entry.Arguments.Select(static (a, i) => a.Type is AddressType ? $"ptr noalias %p{i}" : $"{(a.Type == SilType.Unit ? "{}" : Type(a.Type))} %p{i}");
        var arguments = string.Join(", ", function.Entry.Arguments.Select(static (a, i) => a.Type is AddressType ? $"ptr %p{i}" : $"{(a.Type == SilType.Unit ? "{}" : Type(a.Type))} %p{i}"));
        string Call(string suffix) =>
            type == "void"
                ? $"  tail call void @{Global(function.Name + suffix)}({arguments})\n  ret void\n"
                : $"  %{suffix[1..]}.result = tail call {type} @{Global(function.Name + suffix)}({arguments})\n  ret {type} %{suffix[1..]}.result\n";

        var linkage = Linkage(function.Name);
        var comdat = Comdat("@" + Global(function.Name), linkage) is { Length: > 0 } ? " comdat" : "";
        var inline = function.Inline == SilInline.Never ? " noinline" : "";
        // Asked once for the program, and kept in the runtime's word (1 is AVX2).
        output.Append($"define {linkage}{type} @{Global(function.Name)}({string.Join(", ", parameters)}) nounwind{inline}{comdat} {{\n")
            .Append("entry:\n  %known = load atomic i32, ptr @sable_processor_known monotonic, align 4\n")
            .Append("  %asked = icmp ne i32 %known, 0\n")
            .Append("  br i1 %asked, label %choose, label %ask, !prof !{!\"branch_weights\", i32 1000, i32 1}\n")
            .Append("ask:\n  %answer = call zeroext i1 @sable_vector_available()\n  %found = zext i1 %answer to i32\n  br label %choose\n")
            .Append("choose:\n  %features = phi i32 [ %known, %entry ], [ %found, %ask ]\n")
            .Append("  %wide.bit = and i32 %features, 1\n  %wide = icmp ne i32 %wide.bit, 0\n")
            .Append("  br i1 %wide, label %avx2, label %baseline\n")
            .Append("avx2:\n").Append(Call(WideSuffix))
            .Append("baseline:\n").Append(Call(BaselineSuffix))
            .Append("}\n\n");
    }

    /// <summary>
    ///     Whether the function gives a vector to another function, or takes
    ///     one: in registers, which are not the same ones with AVX2 as without,
    ///     so that the two sides of a call would not agree.
    /// </summary>
    static bool PassesVectors(SilFunction function) =>
        HoldsVector(function.Type.Result) || function.Entry.Arguments.Any(static a => HoldsVector(a.Type))
        || function.Blocks.SelectMany(static b => b.Instructions)
            .Any(static i => i is ApplyInst or TryApplyInst && (HoldsVector(i is TryApplyInst tried ? tried.ResultType : i.Type) || i.Operands.Any(static o => HoldsVector(o.Type))));

    /// <summary>Whether a value of the type is, or has in it, a vector — not behind a reference.</summary>
    static bool HoldsVector(SilType type) =>
        type switch {
            VectorType => true,
            TupleType tuple => tuple.Elements.Any(HoldsVector),
            StructType structType => structType.Fields.Any(static f => HoldsVector(f.Type)),
            EnumType enumType => enumType.Cases.Any(static c => c.Payload is { } payload && HoldsVector(payload)),
            _ => false,
        };
}
