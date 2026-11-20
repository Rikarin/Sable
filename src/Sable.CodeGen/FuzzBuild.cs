using System.Security.Cryptography;
using System.Text;

namespace Sable.CodeGen;

/// <summary>What a fuzz harness is built as: the fuzzer itself, or a program that runs saved inputs once each.</summary>
public enum FuzzEngine {
    /// <summary>
    ///     libFuzzer (<c>-fsanitize=fuzzer</c>): LLVM's SanitizerCoverage
    ///     counts every edge of the program, and libFuzzer's own <c>main</c>
    ///     keeps the inputs that reach new ones, minimises, and replays.
    /// </summary>
    LibFuzzer,

    /// <summary>The same program with a <c>main</c> of its own that runs each file it is given once: on any clang, Apple's too.</summary>
    Replay,
}

/// <summary>
///     Builds a <c>sable fuzz</c> harness (docs/05 §5.1): the program's LLVM
///     IR — a library, whose <c>@export</c> functions take an input a byte at a
///     time and run a target on it (<c>TestHarness</c>) — the runtime, and a C
///     shim, <see cref="Shim" />, that is libFuzzer's entry point. Everything
///     is compiled with AddressSanitizer and UndefinedBehaviorSanitizer, which
///     reach what Sable's own checks do not: the C runtime, and what crosses
///     to C. The Sable code is held to its own checks, whose traps abort.
/// </summary>
/// <remarks>
///     <para>
///         Coverage needs no rewriting of the program: clang's
///         <c>-fsanitize=fuzzer</c> instruments the IR it compiles, Sable's as
///         any other. A trap aborts, which libFuzzer reports as a crash and
///         keeps the input of.
///     </para>
///     <para>
///         One build serves every target of the program: which one runs is
///         <c>SABLE_FUZZ_TARGET</c>, a number, in the environment.
///     </para>
///     <para>
///         libFuzzer's runtime comes with LLVM's clang (Linux's packages have
///         it); Apple's clang has none, so on macOS, unless <c>CC</c> names an
///         LLVM clang, only the replay build can be made (<see cref="MissingLibFuzzer" />).
///     </para>
/// </remarks>
public static class FuzzBuild {
    /// <summary>
    ///     libFuzzer's entry point, which hands each input to the Sable
    ///     harness a byte at a time — only integers cross to C — and runs the
    ///     target <c>SABLE_FUZZ_TARGET</c> numbers on it; and, built with
    ///     <c>-DSABLE_FUZZ_REPLAY</c>, a <c>main</c> that runs each file once,
    ///     saying which before it does, so that the one that brings the
    ///     process down is known.
    /// </summary>
    public const string Shim = """
        /* Written by the Sable compiler for 'sable fuzz'. */
        #include <stddef.h>
        #include <stdint.h>
        #include <stdio.h>
        #include <stdlib.h>

        void sable_harness_fuzz_begin(void);
        void sable_harness_fuzz_push(uint8_t byte);
        void sable_harness_fuzz_run(int64_t target);

        static int64_t sable_fuzz_target(void) {
            static int64_t target = -1;
            if (target < 0) {
                const char *chosen = getenv("SABLE_FUZZ_TARGET");
                target = chosen ? strtoll(chosen, NULL, 10) : 0;
                if (target < 0) {
                    target = 0;
                }
            }
            return target;
        }

        int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
            sable_harness_fuzz_begin();
            for (size_t i = 0; i < size; i++) {
                sable_harness_fuzz_push(data[i]);
            }
            sable_harness_fuzz_run(sable_fuzz_target());
            return 0;
        }

        #ifdef SABLE_FUZZ_REPLAY
        int main(int argc, char **argv) {
            for (int i = 1; i < argc; i++) {
                FILE *file = fopen(argv[i], "rb");
                if (!file) {
                    fprintf(stderr, "sable fuzz: cannot read %s\n", argv[i]);
                    return 2;
                }
                size_t capacity = 4096, size = 0;
                uint8_t *data = malloc(capacity);
                for (size_t n; (n = fread(data + size, 1, capacity - size, file)) > 0;) {
                    size += n;
                    if (size == capacity) {
                        capacity *= 2;
                        data = realloc(data, capacity);
                    }
                }
                fclose(file);
                fprintf(stderr, "Running: %s\n", argv[i]);
                fflush(stderr);
                LLVMFuzzerTestOneInput(data, size);
                free(data);
            }
            return 0;
        }
        #endif

        """;

    /// <summary>
    ///     What starts the line each build writes before it runs an input:
    ///     libFuzzer's own words, which the replay build's <c>main</c> uses too.
    /// </summary>
    public const string RunningPrefix = "Running: ";

    /// <summary>
    ///     clang's flags for the harness: a little optimised, as libFuzzer
    ///     wants it, frames kept for the sanitizers' reports, and the engine's
    ///     sanitizers.
    /// </summary>
    public static IReadOnlyList<string> Flags(FuzzEngine engine) => [
        "-O1", "-g", "-fno-omit-frame-pointer",
        engine == FuzzEngine.LibFuzzer ? "-fsanitize=fuzzer,address,undefined" : "-fsanitize=address,undefined",
        "-fno-sanitize-recover=all", "-Wno-override-module",
        ..(engine == FuzzEngine.Replay ? ["-DSABLE_FUZZ_REPLAY"] : Array.Empty<string>()),
    ];

    /// <summary>
    ///     The whole command line after clang's name: the IR, the runtime and
    ///     the shim, built for this machine into <paramref name="output" />,
    ///     with what the package's build scripts add to the link.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string ir, string runtime, string shim, string output, FuzzEngine engine, NativeTarget host, IReadOnlyList<string>? link = null) =>
        [..host.ClangFlags, ..Flags(engine), ir, runtime, shim, "-o", output, ..NativeBuild.PlatformLinkFlags(host.Platform), ..link ?? []];

    /// <summary>
    ///     Builds the harness's IR into <paramref name="output" />, unless what
    ///     is there was built from the same IR the same way: a key of all that
    ///     goes into it is kept beside it (<c>.key</c>), and fuzzing an
    ///     unchanged program again costs no build.
    /// </summary>
    public static NativeBuildResult Build(string llvmIr, string output, FuzzEngine engine, IReadOnlyList<string>? link = null) {
        var host = NativeTarget.Host;
        if (host.Platform is not (NativePlatform.Linux or NativePlatform.MacOS)) {
            return new(false, $"sable fuzz runs on Linux and macOS, not on {host.Triple}");
        }

        var keyFile = output + ".key";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"sable-fuzz-1\n{NativeObjects.CompilerIdentity}\n{engine}\n{string.Join(' ', link ?? [])}\n{Shim}\n{NativeBuild.RuntimeSource}\n{llvmIr}")));
        if (File.Exists(output) && File.Exists(keyFile) && File.ReadAllText(keyFile) == key) {
            return new(true, "");
        }

        var directory = Directory.CreateTempSubdirectory("sable-fuzz-");
        try {
            var ir = Path.Combine(directory.FullName, "harness.ll");
            File.WriteAllText(ir, NativeBuild.ForTarget(llvmIr, host, namesFrames: true));
            var runtime = NativeBuild.WriteRuntime(Path.Combine(directory.FullName, "runtime"));
            var shim = Path.Combine(directory.FullName, "fuzz.c");
            File.WriteAllText(shim, Shim);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.Delete(keyFile);
            var (exitCode, text) = NativeBuild.Run(NativeBuild.CompilerPath, Arguments(ir, runtime, shim, output, engine, host, link));
            if (exitCode == 0) {
                File.WriteAllText(keyFile, key);
            }

            return new(exitCode == 0, text);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>Whether this machine's clang links <c>-fsanitize=fuzzer</c>: whether it has libFuzzer's runtime. Asked once per process.</summary>
    public static bool HasLibFuzzer => LibFuzzerProbe.Value;

    /// <summary>Whether this machine's clang links AddressSanitizer and UBSan, which the replay build needs. Asked once per process.</summary>
    public static bool HasSanitizers => SanitizerProbe.Value;

    static readonly Lazy<bool> LibFuzzerProbe = new(static () =>
        Links("#include <stddef.h>\n#include <stdint.h>\nint LLVMFuzzerTestOneInput(const uint8_t *d, size_t n) { return 0; }\n", "-fsanitize=fuzzer"));

    static readonly Lazy<bool> SanitizerProbe = new(static () => Links("int main(void) { return 0; }\n", "-fsanitize=address,undefined"));

    static bool Links(string source, string flag) {
        var directory = Directory.CreateTempSubdirectory("sable-probe-");
        try {
            var probe = Path.Combine(directory.FullName, "probe.c");
            File.WriteAllText(probe, source);
            return NativeBuild.Run(NativeBuild.CompilerPath, [flag, probe, "-o", Path.Combine(directory.FullName, "probe")]).ExitCode == 0;
        } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) {
            return false;
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     Why there is no fuzzing here, and what to do about it: said when the
    ///     C compiler has no libFuzzer runtime.
    /// </summary>
    /// <param name="compiler">The C compiler asked (<c>$CC</c>, or <c>clang</c>).</param>
    /// <param name="version">What <see cref="NativeBuild.CompilerVersion" /> says of it.</param>
    /// <param name="platform">The machine's platform.</param>
    public static string MissingLibFuzzer(string compiler, (int Major, bool Apple)? version, NativePlatform platform) {
        var which = version switch {
            { Apple: true } v => $"'{compiler}' is Apple's clang {v.Major}, which has no libFuzzer runtime",
            { } v => $"'{compiler}' (clang {v.Major}) cannot link -fsanitize=fuzzer: it has no libFuzzer runtime",
            null => $"'{compiler}' cannot link -fsanitize=fuzzer: it has no libFuzzer runtime",
        };
        var remedy = platform switch {
            NativePlatform.MacOS => "LLVM's own clang has one: 'brew install llvm', then set CC=\"$(brew --prefix llvm)/bin/clang\"",
            NativePlatform.Linux => "install clang's runtime libraries (on Debian and Ubuntu, libclang-rt-<version>-dev) or set CC to a clang that has them",
            _ => "set CC to an LLVM clang that has it",
        };
        return $"sable fuzz: {which}.\n{remedy}.\n'sable fuzz --replay' still runs the saved inputs once each, under AddressSanitizer and UBSan, with any clang.";
    }
}
