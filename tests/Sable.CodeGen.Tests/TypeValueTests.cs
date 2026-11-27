using System.Diagnostics;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Types as values (SYNTAX.md "Metatypes", docs/06 §6.2, #297 stage 3):
///     <c>T.self</c> and <c>type(of: x)</c> as a <c>Type</c>, which points at
///     the type's metadata — an object's own class read from its header, what
///     an existential's box holds, any other value's static type — and is
///     Equatable, Hashable and written by its name; and tests of a value
///     against a type known only at run time. Native and interpreted, which
///     must agree, leak-checked.
/// </summary>
[Native]
public sealed class TypeValueTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    const string Declarations = """
        protocol Named {
            fn name() -> String
        }

        protocol Sized {
            var size: Int { get }
        }

        struct Point: Named {
            var x: Int
            fn name() -> String => "point"
        }

        enum Colour { Red, Green }

        union Shape {
            Circle(Int)
            Dot
        }

        struct Box<T> {
            var item: T
        }

        open class Animal: Named {
            open fn name() -> String => "animal"
        }

        class Dog: Animal, Sized {
            override fn name() -> String => "dog"
            var size: Int => 30
        }

        class Puppy: Dog {}

        struct Lost: Error {}

        fn typeOf<T>(_ value: T) -> Type => type(of: value)
        """;

    [Fact]
    public void TypeOfAValueIsItsStaticTypeForEveryKindButAClass() =>
        Prints(
            """
            Int
            Test.Point
            Test.Colour
            Test.Shape
            (Int, String)
            Int?
            [Int]
            (Int) -> Bool
            Test.Box<String>
            String
            Bool
            Type

            """,
            Declarations,
            """
                print(type(of: 1))
                print(type(of: Point { x: 1 }))
                print(type(of: Colour.Red))
                print(type(of: Shape.Dot))
                print(type(of: (1, "a")))
                let maybe: Int? = 3
                print(type(of: maybe))
                let xs: Int[>..] = [1]
                print(type(of: xs))
                let f: (Int) -> Bool = { n in n > 0 }
                print(type(of: f))
                print(type(of: Box { item: "s" }))
                print(typeOf("s"))
                print(typeOf(true))
                print(type(of: Int.self))
            """
        );

    /// <summary>An object's type is its own class, read from its header: a subclass of the variable's.</summary>
    [Fact]
    public void TypeOfAnObjectIsItsDynamicClass() =>
        Prints(
            """
            Test.Puppy
            Test.Dog
            Test.Animal
            true false
            Test.Puppy

            """,
            Declarations,
            """
                let animals: Animal[>..] = [Puppy(), Dog(), Animal()]
                for a in animals {
                    print(type(of: a))
                }
                print("\(type(of: animals[0]) == Puppy.self) \(type(of: animals[0]) == Animal.self)")
                print(typeOf(animals[0]))
            """
        );

    /// <summary>An existential's type is what its box holds: an object's own class, for an object; an error's, for 'any Error'.</summary>
    [Fact]
    public void TypeOfAnExistentialIsWhatItHolds() =>
        Prints(
            """
            Test.Point
            Test.Dog
            Test.Puppy
            Int
            Test.Lost

            """,
            Declarations,
            """
                let named: (any Named)[>..] = [Point { x: 1 }, Dog()]
                for n in named {
                    print(type(of: n))
                }
                let animal: Animal = Puppy()
                let held: any Named = animal
                print(type(of: held))
                let e: any Equatable = 42
                print(type(of: e))
                do {
                    throw Lost()
                } catch {
                    print(type(of: error))
                }
            """
        );

    [Fact]
    public void ATypeIsEqualHashedAndWrittenByItsName() =>
        Prints(
            """
            true false false
            true
            [Test.Point]|Test.Point  |  Test.Point
            Test.Box<Int> Test.Box<String> false

            """,
            Declarations,
            """
                let t: Type = Point.self
                print("\(t == type(of: Point { x: 2 })) \(t == Int.self) \(t != Point.self)")
                let same: Type = Point.self
                print(t.hashValue == same.hashValue)
                print("[\(t)]|\(t:<12)|\(t:>12)")
                let a: Type = Box<Int>.self
                let b: Type = Box<String>.self
                print("\(a) \(b) \(a == b)")
            """
        );

    /// <summary>A dictionary keyed by types: the done-when of #297, a type of(x) stored and found again.</summary>
    [Fact]
    public void ADictionaryIsKeyedByRuntimeTypes() =>
        Prints(
            """
            dog
            point
            int
            none
            3

            """,
            Declarations,
            """
                var made: [Type: String] = [:]
                let animal: Animal = Dog()
                let t: Type = type(of: animal)
                made[t] = "dog"
                made[Point.self] = "point"
                made[type(of: 7)] = "int"
                print(made[Dog.self] ?? "none")
                print(made[type(of: Point { x: 0 })] ?? "none")
                print(made[Int.self] ?? "none")
                print(made[Animal.self] ?? "none")
                print(made.count)
            """
        );

    /// <summary>
    ///     A value tested against a type known only at run time: the same type
    ///     exactly, a superclass by the chain of superclasses, and a protocol
    ///     (or each of a composition's) by the conformance registry — a
    ///     superclass's conformance counts.
    /// </summary>
    [Fact]
    public void AValueIsTestedAgainstARuntimeType() =>
        Prints(
            """
            Animal: y y y n
            Dog: y y n n
            Puppy: y n n n
            any Named: y y y y
            any Sized: y y n n
            any Named & Sized: y y n n
            Int: n n n n
            true false true false

            """,
            Declarations,
            """
                let wanted: Type[>..] = [
                    Animal.self, Dog.self, Puppy.self, (any Named).self, (any Sized).self, (any Named & Sized).self, Int.self,
                ]
                let values: (any Named)[>..] = [Puppy(), Dog(), Animal(), Point { x: 1 }]
                for t in wanted {
                    var line = "\(t.name.replacing("Test.", with: "")):"
                    for v in values {
                        line += t.isInstance(v) ? " y" : " n"
                    }
                    print(line)
                }
                print("\(Puppy.self.isSubtype(of: Animal.self)) \(Animal.self.isSubtype(of: Puppy.self)) \(Point.self.isSubtype(of: (any Named).self)) \(Point.self.isSubtype(of: (any Sized).self))")
            """
        );

    /// <summary>
    ///     Records from two images — a Sable shared library and the program,
    ///     each with its own copy of a type's — are one type by kind, size and
    ///     name, and hash alike; a protocol of an existential is found by name;
    ///     a superclass by the chain.
    /// </summary>
    [Fact]
    public void TypesFromTwoImagesAreEqualByName() {
        if (!CanBuild) {
            return;
        }

        // A record's pointers to data are SableRefs (#300): on macOS and
        // Linux the distance from the field, set here as the program starts.
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

                         bool sable_metadata_equal(const SableTypeMetadata *, const SableTypeMetadata *);
                         uint64_t sable_metadata_hash(const SableTypeMetadata *);
                         bool sable_metadata_is_subtype(const SableTypeMetadata *, const SableTypeMetadata *);

                         static SableProtocol named;
                         static SableConformance point_records[1];
                         static SableTypeMetadata point = { 2, 0, {0}, 1, {0}, {0}, 16, 8, 16, 1, 0, 0, {0}, {0} };
                         static SableTypeMetadata point_elsewhere = { 2, 0, {0}, 0, {0}, {0}, 16, 8, 16, 1, 0, 0, {0}, {0} };
                         static SableTypeMetadata other = { 2, 0, {0}, 0, {0}, {0}, 16, 8, 16, 1, 0, 0, {0}, {0} };
                         static SableTypeMetadata any_named = { 10, 0, {0}, 0, {0}, {0}, 16, 8, 16, 0, 0, 0, {0}, {0} };
                         static SableTypeMetadata any_any = { 10, 0, {0}, 0, {0}, {0}, 8, 8, 8, 0, 0, 0, {0}, {0} };
                         static SableTypeMetadata animal_type = { 1, 0, {0}, 0, {0}, {0}, 8, 8, 8, 0, 0, 0, {0}, {0} };
                         static SableTypeMetadata dog_type = { 1, 0, {0}, 0, {0}, {0}, 8, 8, 8, 0, 0, 0, {0}, {0} };
                         static const SableMetadata animal = { 0, "Zoo.Animal", 0, 0, 0, 16, 8, 0, &animal_type };
                         static const SableMetadata dog = { 0, "Zoo.Dog", 0, &animal, 0, 16, 8, 0, &dog_type };

                         int main(void) {
                             set(&named.name, "Zoo.Named");
                             set(&point_records[0].protocol, &named);
                             set(&point.conformances, point_records);
                             set(&point.name, "Zoo.Point");
                             set(&point_elsewhere.name, "Zoo.Point");
                             set(&other.name, "Zoo.Other");
                             set(&any_named.name, "any Zoo.Named");
                             set(&any_any.name, "any Any");
                             set(&animal_type.name, "Zoo.Animal");
                             set(&animal_type.object, &animal);
                             set(&dog_type.name, "Zoo.Dog");
                             set(&dog_type.object, &dog);
                             printf("%d %d %d %d %d %d %d %d\n",
                                 sable_metadata_equal(&point, &point_elsewhere),
                                 sable_metadata_hash(&point) == sable_metadata_hash(&point_elsewhere),
                                 sable_metadata_equal(&point, &other),
                                 sable_metadata_is_subtype(&point, &any_named),
                                 sable_metadata_is_subtype(&other, &any_named),
                                 sable_metadata_is_subtype(&other, &any_any),
                                 sable_metadata_is_subtype(&dog_type, &animal_type),
                                 sable_metadata_is_subtype(&animal_type, &dog_type));
                             return 0;
                         }
                         """;

        var directory = Directory.CreateTempSubdirectory("sable-types-");
        try {
            var source = Path.Combine(directory.FullName, "types.c");
            File.WriteAllText(source, c);
            var runtime = Path.Combine(directory.FullName, "runtime.o");
            Assert.True(NativeObjects.CompileRuntime(runtime, true, false).Succeeded);
            var executable = Platform.Executable(Path.Combine(directory.FullName, "types"));
            var linked = NativeObjects.Link([..NativeTarget.Host.ClangFlags, source, runtime], executable, NativeOptions.Of(true, false));
            Assert.True(linked.Succeeded, linked.Output);

            using var run = Process.Start(new ProcessStartInfo(executable) { RedirectStandardOutput = true }.Utf8())!;
            var output = run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            Assert.Equal("1 1 0 1 0 1 1 0\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
        } finally {
            directory.Delete(true);
        }
    }
}
