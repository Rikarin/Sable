using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Profile-guided devirtualisation end to end (SIL's CallProfile.cs, the
///     runtime's profile.c): a build that counts its calls writes what they
///     reached as it ends, and a build given that guesses, with the same
///     output whether the guess is right or wrong.
/// </summary>
[Native]
public sealed class ProfileGuidedTests {
    const string Source = """
        module Guess

        import Sable.IO

        open class Shape {
            open fn area() -> Int { return 0 }
        }

        final class Square: Shape {
            override fn area() -> Int { return 4 }
        }

        final class Circle: Shape {
            override fn area() -> Int { return 3 }
        }

        final class Dot: Shape {
            override fn area() -> Int { return 1 }
        }

        fn sum(_ shapes: Shape[>..]) -> Int {
            var total = 0
            for shape in shapes {
                total = total + shape.area()
            }
            return total
        }

        fn each(_ values: Int[>..], _ f: (Int) -> Int) -> Int {
            var total = 0
            for value in values {
                total = total + f(value)
            }
            return total
        }

        @main
        fn main() {
            // With an argument, the other kinds: what the profile did not see.
            let other = Environment.arguments().count > 1
            var shapes: Shape[>..] = []
            var values: Int[>..] = []
            for i in 0..<1000 {
                if other && i % 2 == 0 {
                    shapes.append(Circle())
                } else {
                    shapes.append(Square())
                }
                values.append(i)
            }
            let k = shapes.count
            if other {
                print("\(sum(shapes)) \(each(values) { $0 - k })")
            } else {
                print("\(sum(shapes)) \(each(values) { $0 + k })")
            }
        }
        """;

    static string Build(string directory, string name, ISilPass? profile) {
        var tree = SyntaxTree.Parse(new SourceText(Source, "guess.sb"), Pipeline.ParseOptionsFor(true));
        var result = Pipeline.Compile([tree], optimised: true, checkExclusivity: false, profile: profile);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var executable = Platform.Executable(Path.Combine(directory, name));
        var build = NativeBuild.Build(result.LlvmIr!, executable, optimise: true);
        Assert.True(build.Succeeded, build.Output);
        return executable;
    }

    static string Run(string executable, params string[] arguments) {
        var start = new ProcessStartInfo(executable, arguments) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
        start.Environment.Remove("SABLE_PROFILE_FILE");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Result;
    }

    [Fact]
    public void ACountedRunGuidesTheNextBuild() {
        if (!NativeBuild.IsAvailable()) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-profile-");
        try {
            var file = Path.Combine(directory.FullName, "guess.sableprof");
            var plain = Build(directory.FullName, "plain", null);
            var counting = Build(directory.FullName, "counting", new InstrumentProfile(file));
            Assert.Equal(Run(plain), Run(counting));

            // What the counted run reached: each site's calls, and the function that took them.
            var text = File.ReadAllText(file);
            Assert.StartsWith(CallProfile.Header + "\n", text, StringComparison.Ordinal);
            Assert.Matches(@"site\tGuess\.sum#0@[0-9a-f]{16}\t1000\ntarget\t1000\tGuess\.Square\.area\n", text);
            Assert.Matches(@"site\tGuess\.each#0@[0-9a-f]{16}\t1000\ntarget\t1000\tGuess\.main\$closure1\n", text);

            var data = ProfileData.Parse(text, out var problem);
            Assert.Null(problem);
            var use = new UseProfile(data!);
            var guessed = Build(directory.FullName, "guessed", use);
            Assert.Equal(2, use.Guessed);
            Assert.Equal(Run(plain), Run(guessed));
            Assert.Equal(Run(plain, "other"), Run(guessed, "other"));
        } finally {
            directory.Delete(true);
        }
    }
}
