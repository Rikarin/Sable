// Port of thread-spawn.sb: a Thread started and joined.
[Benchmark("library/thread-spawn")]
static class LibraryThreadSpawn {
    public static string Run() {
        var total = 0;
        for (var n = 0; n < 2000; n++) {
            var given = 0;
            var captured = n;
            var thread = new Thread(() => given = captured & 3);
            thread.Start();
            thread.Join();
            total += given;
        }

        return total.ToString();
    }
}
