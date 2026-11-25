// Port of startup.sb, in the runner the other ports are in.
[Benchmark("programs/startup")]
static class ProgramsStartup {
    public static string Run() => "started";
}
