namespace Sable.CodeGen.Tests;

// TEMPORARY: runs $SABLE_SCRATCH in the interpreter, writing what it printed,
// its trap and its leaks to $SABLE_SCRATCH.out, and its SIL to .sil.
public sealed class ScratchRun {
    [Fact]
    public void Run() {
        if (Environment.GetEnvironmentVariable("SABLE_SCRATCH") is not { Length: > 0 } path) {
            return;
        }

        var result = Pipeline.Compile(new SourceText(File.ReadAllText(path), "test.sb"));
        if (!result.Succeeded) {
            File.WriteAllText(path + ".out", string.Join("\n", result.Errors));
            return;
        }

        File.WriteAllText(path + ".sil", Sable.SIL.SilPrinter.Print(result.Sil!));
        var interpreter = new Sable.SIL.SilInterpreter(result.Sil!);
        string trap = "";
        try {
            interpreter.Run(result.Sil!.EntryPoint!);
        } catch (Sable.SIL.SilTrap t) {
            trap = "TRAP: " + t.Message;
        }

        File.WriteAllText(path + ".out", interpreter.Output + "\n" + trap + "\nlive: " + interpreter.LiveObjects + "\nallocations: " + interpreter.Allocations + "\n");
    }
}
