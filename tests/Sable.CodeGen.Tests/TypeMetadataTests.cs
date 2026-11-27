using System.Diagnostics;
using System.Text.RegularExpressions;
using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A class's type metadata (docs/06 §6.2): field descriptors,
///     conformance records and the registry's lookup, and what
///     <c>--deny-reflection</c> leaves out (#218). Every other kind's metadata
///     is <see cref="TypeKindTests" />'s.
/// </summary>
[Native]
public sealed class TypeMetadataTests {
    const string Classes = """
                           module Zoo

                           protocol Named {
                               fn name() -> String
                           }

                           protocol Pet: Named {}

                           protocol Loud {}

                           class Animal: Pet {
                               var legs: Int = 4
                               var friend: Animal? = none
                               fn name() -> String => "animal"
                           }

                           class Dog: Animal {
                               var tag: String = "rex"
                           }

                           extension Dog: Loud {}

                           @main
                           fn main() {
                               let dog = Dog()
                               print(dog.legs)
                           }
                           """;

    internal static string Ir(string source, bool denyReflection = false) {
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(source, "zoo.sb"))], denyReflection: denyReflection);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        // Read with its symbols as their SIL names (SymbolNames).
        return SymbolNames.DemangleIr(result.LlvmIr!);
    }

    static string Line(string ir, string global) =>
        ir.Split('\n').Single(l => l.StartsWith(global + " = ", StringComparison.Ordinal));

    // A record's pointer to data (#300): the distance from its field on macOS
    // and Linux, the address on Windows; and its type, and null, there.
    static readonly bool Relative = !OperatingSystem.IsWindows();
    static readonly string RefType = Relative ? "i64" : "ptr";
    static readonly string Null = Relative ? "i64 0" : "ptr null";

    static string Ref(string target) => Relative ? $"i64 sub (i64 ptrtoint (ptr {target} to i64)" : $"ptr {target}";

    // A pointer at a string constant, whatever its number.
    static readonly string TrapRef = Relative ? "i64 sub (i64 ptrtoint (ptr @.trap." : "ptr @.trap.";

    [Fact]
    public void AClassesMetadataDescribesItsFieldsAndConformances() {
        var ir = Ir(Classes);

        // Every stored field, its superclass's first, with what it holds:
        // data (0), a strong reference (1).
        var fields = Line(ir, "@\"sable.fields.Zoo.Dog\"");
        Assert.Contains($"[3 x {{ {RefType}, {RefType}, i64, i64, {RefType} }}]", fields, StringComparison.Ordinal);
        Assert.Contains(TrapRef, fields, StringComparison.Ordinal);
        Assert.Matches($@"i32 3\) to i64\), i64 0, {Null} \}}.*i32 4\) to i64\), i64 1, {Null} \}}.*i32 5\) to i64\), i64 1, {Null} \}}", fields);
        Assert.Contains("c\"friend\\00\"", ir, StringComparison.Ordinal);
        Assert.Contains("c\"Zoo.Animal?\\00\"", ir, StringComparison.Ordinal);

        // A class's own conformances — with what they refine, and an
        // extension's — and its superclass's in its superclass's records.
        var animal = Line(ir, "@\"sable.conformances.Zoo.Animal\"");
        Assert.Contains($"[2 x {{ {RefType}, {RefType} }}]", animal, StringComparison.Ordinal);
        Assert.Matches($"{Regex.Escape(Ref("@\"sable.protocol.Zoo.Pet\""))}.*{Regex.Escape(Ref("@\"sable.protocol.Zoo.Named\""))}", animal);
        Assert.Equal(2, Regex.Matches(animal, Regex.Escape($", {Null} }}")).Count);
        var dog = Line(ir, "@\"sable.conformances.Zoo.Dog\"");
        Assert.Contains($"[1 x {{ {RefType}, {RefType} }}]", dog, StringComparison.Ordinal);
        Assert.Contains(Ref("@\"sable.protocol.Zoo.Loud\""), dog, StringComparison.Ordinal);
        // Its type metadata: a class (1), three fields, one conformance
        // record, then its name and the rest — its object metadata last.
        var type = Line(ir, "@\"sable.type.Zoo.Dog\"");
        Assert.StartsWith($"@\"sable.type.Zoo.Dog\" = internal constant %SableTypeMetadata {{ i64 1, i64 3, {Ref("@\"sable.fields.Zoo.Dog\"")}", type, StringComparison.Ordinal);
        Assert.Matches($"{Regex.Escape(Ref("@\"sable.fields.Zoo.Dog\""))}.*, i64 1, {Regex.Escape(Ref("@\"sable.conformances.Zoo.Dog\""))}.*{Regex.Escape(TrapRef)}", type);
        Assert.Contains($"ptr @sable_reference_witnesses, i64 0, {Null}, {Ref("@\"sable.meta.Zoo.Dog\"")}", type, StringComparison.Ordinal);

        // The object metadata points at it, before the vtable.
        Assert.Contains("ptr @\"sable.type.Zoo.Dog\", [", Line(ir, "@\"sable.meta.Zoo.Dog\""), StringComparison.Ordinal);
    }

    [Fact]
    public void DenyingReflectionLeavesTheDescriptorsWithoutNames() {
        var ir = Ir(Classes, denyReflection: true);
        Assert.Contains($"{{ {RefType}, {RefType}, i64, i64, {RefType} }} {{ {Null}, {Null}, i64 ptrtoint", Line(ir, "@\"sable.fields.Zoo.Dog\""), StringComparison.Ordinal);
        Assert.DoesNotContain("c\"friend\\00\"", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void DenyingReflectionRefusesAMirror() {
        const string source = """
                              module M

                              struct Point {
                                  var x: Int
                              }

                              @main
                              fn main() {
                                  let m = Mirror(of: Point(x: 1))
                                  print(m.count)
                              }
                              """;
        var tree = SyntaxTree.Parse(new SourceText(source, "m.sb"));
        Assert.True(Pipeline.Compile([tree]).Succeeded);

        var denied = Pipeline.Compile([tree], denyReflection: true);
        Assert.Contains(denied.Errors, static e => e.Contains("S3450", StringComparison.Ordinal) && e.Contains("--deny-reflection", StringComparison.Ordinal));
        Assert.Equal("S3450", $"S{(int)ErrorCode.ERR_ReflectionDenied}");
    }

    /// <summary>
    ///     The registry's lookup, against metadata laid out as docs/06 §6.2
    ///     says: a class's own records, then its superclass's; a struct's
    ///     records, with their witness tables; a protocol matched by its
    ///     descriptor or, from another image, by its name.
    /// </summary>
    [Fact]
    public void TheRegistryFindsAConformanceThroughTheSuperclasses() {
        if (!Programs.CanBuild) {
            return;
        }

        // A record's pointers to data are SableRefs (#300): on macOS and
        // Linux the distance from the field, which a record made here, as the
        // runtime makes its own, sets as it starts; on Windows the address.
        const string c = """
                         #include <stdint.h>
                         #include <stdio.h>
                         #include <stdbool.h>

                         typedef struct { uintptr_t bits; } Ref;
                         static void set(Ref *ref, const void *to) {
                         #if defined(_WIN32)
                             ref->bits = (uintptr_t)to;
                         #else
                             ref->bits = to == 0 ? 0 : (uintptr_t)((const char *)to - (const char *)ref);
                         #endif
                         }

                         typedef struct { Ref name; } SableProtocol;
                         typedef struct { Ref protocol; Ref witnesses; } SableConformance;
                         struct SableMetadata;
                         typedef struct {
                             int64_t kind, field_count; Ref fields; int64_t conformance_count; Ref conformances;
                             Ref name; int64_t size, alignment, stride, flags; const void *witnesses;
                             int64_t argument_count; Ref arguments; Ref object;
                         } SableTypeMetadata;
                         typedef struct SableMetadata {
                             void (*destroy)(void *); const char *name; int64_t element_size;
                             const struct SableMetadata *superclass; void (*destroy_element)(void *);
                             int64_t size, alignment; void (*visit)(void *); const SableTypeMetadata *type;
                         } SableMetadata;

                         const SableConformance *sable_conformance(const SableMetadata *, const SableProtocol *);
                         const SableConformance *sable_type_conformance(const SableTypeMetadata *, const SableProtocol *);
                         const void *sable_witness_table(const SableTypeMetadata *, const SableProtocol *);
                         bool sable_conforms(const void *, const SableProtocol *);

                         static SableProtocol named, loud, other, named_elsewhere;
                         static const int witnesses = 7;
                         static SableConformance animal_records[1], dog_records[1], point_records[1];
                         static SableTypeMetadata animal_type = { 1, 0, {0}, 1, {0}, {0}, 8, 8, 8, 0, 0, 0, {0}, {0} };
                         static SableTypeMetadata dog_type = { 1, 0, {0}, 1, {0}, {0}, 8, 8, 8, 0, 0, 0, {0}, {0} };
                         static SableTypeMetadata point_type = { 2, 0, {0}, 1, {0}, {0}, 16, 8, 16, 1, 0, 0, {0}, {0} };
                         static const SableMetadata animal = { 0, "Zoo.Animal", 0, 0, 0, 16, 8, 0, &animal_type };
                         static const SableMetadata dog = { 0, "Zoo.Dog", 0, &animal, 0, 16, 8, 0, &dog_type };
                         static const SableMetadata array = { 0, "[Int]", 8, 0, 0, 32, 8, 0, 0 };

                         static void conformance(SableConformance *record, const SableProtocol *protocol, const void *table) {
                             set(&record->protocol, protocol);
                             set(&record->witnesses, table);
                         }

                         static void type(SableTypeMetadata *type, SableConformance *records, const char *name, const SableMetadata *object) {
                             set(&type->conformances, records);
                             set(&type->name, name);
                             set(&type->object, object);
                         }

                         int main(void) {
                             set(&named.name, "Zoo.Named");
                             set(&loud.name, "Zoo.Loud");
                             set(&other.name, "Zoo.Other");
                             set(&named_elsewhere.name, "Zoo.Named");
                             conformance(&animal_records[0], &named, &witnesses);
                             conformance(&dog_records[0], &loud, 0);
                             conformance(&point_records[0], &named, &witnesses);
                             type(&animal_type, animal_records, "Zoo.Animal", &animal);
                             type(&dog_type, dog_records, "Zoo.Dog", &dog);
                             type(&point_type, point_records, "Zoo.Point", 0);
                             const void *object[2] = { &dog, 0 };
                             const SableConformance *found = sable_conformance(&dog, &named_elsewhere);
                             printf("%d %d %d %d %d %d %d %d\n",
                                 sable_conformance(&dog, &loud) == &dog_records[0],
                                 sable_conformance(&dog, &named) == &animal_records[0],
                                 (const char *)&found->witnesses + found->witnesses.bits == (const char *)&witnesses || found->witnesses.bits == (uintptr_t)&witnesses,
                                 sable_conformance(&animal, &loud) == 0,
                                 sable_conforms(object, &other),
                                 sable_conformance(&array, &named) == 0,
                                 sable_witness_table(&point_type, &named_elsewhere) == &witnesses,
                                 sable_type_conformance(&point_type, &loud) == 0);
                             return 0;
                         }
                         """;

        var directory = Directory.CreateTempSubdirectory("sable-registry-");
        try {
            var source = Path.Combine(directory.FullName, "registry.c");
            File.WriteAllText(source, c);
            var runtime = Path.Combine(directory.FullName, "runtime.o");
            Assert.True(NativeObjects.CompileRuntime(runtime, true, false).Succeeded);
            var executable = Platform.Executable(Path.Combine(directory.FullName, "registry"));
            var linked = NativeObjects.Link([..NativeTarget.Host.ClangFlags, source, runtime], executable, NativeOptions.Of(true, false));
            Assert.True(linked.Succeeded, linked.Output);

            using var run = Process.Start(new ProcessStartInfo(executable) { RedirectStandardOutput = true }.Utf8())!;
            var output = run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            Assert.Equal("1 1 1 1 0 1 1 1\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     'animal is Named' asks the registry of the object's dynamic class;
    ///     'animal as? any Named' puts the object in a box and asks the cast
    ///     what any box holding an object is asked. Native and interpreted.
    /// </summary>
    [Fact]
    public void AnObjectIsTestedAndCastToAProtocolByItsDynamicClass() {
        var source = Programs.Program(
            """
            protocol Named {
                fn name() -> String
            }

            protocol Loud {}

            class Animal {}

            class Dog: Animal, Named {
                fn name() -> String => "dog"
            }

            class Puppy: Dog, Loud {}

            fn describe(_ animal: Animal) -> String {
                var text = "\(animal is Named) \(animal is Loud) \(animal is Named & Loud)"
                if let named = animal as? any Named {
                    text = text + " " + named.name()
                }
                return text
            }
            """,
            """
                print(describe(Animal()))
                print(describe(Dog()))
                print(describe(Puppy()))
                let loud = Puppy() as! any Named
                print(loud.name())
            """
        );
        Assert.Equal(
            "false false false\ntrue false false dog\ntrue true true dog\ndog\n",
            Programs.AssertAgree(source).Stdout
        );
        Assert.Contains("@sable_conforms(", Pipeline.Compile(new SourceText(source, "test.sb")).LlvmIr!, StringComparison.Ordinal);
    }
}

/// <summary><c>[targets.&lt;triple&gt;]</c> (docs/05 §5.2.10): a target's reflection setting.</summary>
public sealed class TargetSettingsTests {
    /// <summary>This machine as a triple <c>--target</c> takes.</summary>
    static string HostTriple => (NativeTarget.Host.Platform, NativeTarget.Host.Arch) switch {
        (NativePlatform.MacOS, "arm64") => "aarch64-apple-darwin",
        (NativePlatform.MacOS, _) => "x86_64-apple-darwin",
        (NativePlatform.Linux, "arm64") => "aarch64-unknown-linux-gnu",
        (NativePlatform.Linux, _) => "x86_64-unknown-linux-gnu",
        _ => "x86_64-pc-windows-msvc",
    };

    [Fact]
    public void AManifestSaysWhatABuildForATargetCarries() {
        using var fixture = new Sable.CodeGen.Tests.PackageFixture();
        fixture.Package("p", "p", "0.1.0", "[targets.wasm32-wasi]\nreflection = false\n\n[targets.x86_64-unknown-linux-gnu]\nreflection = true\n", ("src/lib.sb", "module P\n"));
        var manifest = fixture.Load("p").RootManifest;
        Assert.False(manifest.TargetSettingsFor(NativeTarget.Of("wasm32-wasi"))!.Reflection);
        Assert.True(manifest.TargetSettingsFor(NativeTarget.Of("x86_64-unknown-linux-gnu"))!.Reflection);
        Assert.Null(manifest.TargetSettingsFor(NativeTarget.Of("aarch64-unknown-linux-gnu")));

        fixture.Package("q", "q", "0.1.0", "[targets.nowhere]\nreflection = false\n", ("src/lib.sb", "module Q\n"));
        Assert.Contains(Assert.Throws<Sable.Driver.Packages.ManifestException>(() => fixture.Load("q")).Errors, static e => e.Key == "targets.nowhere");

        fixture.Write("r/package.sb", """
                                      import Sable.Build

                                      let manifest = Package {
                                          name: "r",
                                          version: "0.1.0",
                                          targetSettings: [.target("wasm32-wasi", reflection: false)],
                                      }
                                      """);
        fixture.Write("r/src/lib.sb", "module R\n");
        Assert.False(Sable.Driver.Packages.ManifestFiles.Load(Path.Combine(fixture.Root, "r")).TargetSettingsFor(NativeTarget.Of("wasm32-wasi"))!.Reflection);
    }

    /// <summary>A package built for a target its manifest denies reflection refuses a mirror, as --deny-reflection does.</summary>
    [Fact]
    public void APackageBuiltWithoutReflectionRefusesAMirror() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new Sable.CodeGen.Tests.PackageFixture();
        const string main = "module Main\n\nstruct Point {\n    var x: Int\n}\n\nfn main() {\n    print(Mirror(of: Point(x: 1)).count)\n}\n";
        fixture.Package("app", "app", "0.1.0", "", ("src/main.sb", main));
        Assert.NotNull(Sable.Driver.Packages.PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new()));

        fixture.Package("app", "app", "0.1.0", $"[targets.{HostTriple}]\nreflection = false\n", ("src/main.sb", main));
        Assert.Null(Sable.Driver.Packages.PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new()));
        Assert.Contains("S3450", fixture.Errors.ToString(), StringComparison.Ordinal);
    }
}
