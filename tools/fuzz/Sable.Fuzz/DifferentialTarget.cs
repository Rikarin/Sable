using Sable.CodeGen.Tests;

namespace Sable.Fuzz;

/// <summary>
///     A small program the bytes choose (<see cref="ProgramGenerator" />), run
///     in the interpreter as it is lowered, with its generics unspecialised,
///     through the optimising passes and against the library's interface: all
///     must print the same and trap the same (<see cref="DifferentialCheck" />).
///     Native code, which a clang makes, is the tests' (DifferentialFuzzTests).
/// </summary>
public sealed class DifferentialTarget : Target {
    public override string Name => "differential";

    public override int MaxLength => 1024;

    public override TimeSpan Limit => TimeSpan.FromSeconds(60);

    public override void Run(ReadOnlySpan<byte> data) => DifferentialCheck.Check(ProgramGenerator.Generate(data));

    public override IEnumerable<(string Name, byte[] Data)> Seeds(string root) =>
        Enumerable.Range(0, 24).Select(static i => ($"program-{i}", ProgramGenerator.Choices(i, 60 + i * 30)));

    // The bytes are numbers, each what is left of it by how many there are to choose from.
    public override IEnumerable<string> Dictionary() => [];
}
