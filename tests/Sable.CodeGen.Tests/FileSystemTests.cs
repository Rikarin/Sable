using Sable.Symbols;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The file system (Sable.IO over runtime/fs.c and runtime/watch.c),
///     natively and interpreted, which must do the same and fail the same
///     way. The library's own tests (tests/lib/Sable.IO) say more, natively.
///     Every program works in a directory of its own making.
/// </summary>
[Native]
public sealed class FileSystemTests {
    static string Program(string body) => $$"""
        module P
        import Sable.Concurrency
        import Sable.IO
        import Sable.Time

        fn attempt(_ what: String, _ body: () throws(IoError) -> Void) {
            do {
                try body()
                print("\(what): done")
            } catch let error {
                let why = match error {
                    case .NotFound(_)           => "not found"
                    case .PermissionDenied(_)   => "permission"
                    case .AlreadyExists(_)      => "exists"
                    case .IsDirectory(_)        => "a directory"
                    case .InvalidInput(_)       => "invalid"
                    case .DirectoryNotEmpty(_)  => "not empty"
                    case .NotADirectory(_)      => "not a directory"
                    case .CrossesFileSystems(_) => "crosses"
                    case .Unsupported(_)        => "unsupported"
                    case .Os(_, _)              => "the system's"
                    case _                      => "another"
                }
                print("\(what): \(why)")
            }
        }

        @main
        fn main() async throws {
            let temp = try Temp.directory()
            let root = temp.path
        {{body}}
        }
        """;

    static string Agree(string body) => AssertAgree(Program(body)).Stdout;

    [Fact]
    public void FilesAreCopiedRenamedAndRemoved() =>
        Assert.Equal(
            """
            over: exists
            itself: invalid
            directory: a directory
            missing: not found
            12 true
            rename over: exists
            rename: done
            one
            two there
            remove directory: a directory
            remove missing: not found
            remove '..': invalid
            removed: done
            false

            """.ReplaceLineEndings("\n"),
            Agree("""
                let a = root.appending("a")
                let b = root.appending("b")
                try File.write(a, "twelve bytes")
                try File.setPermissions(a, FilePermissions(mode: 0o444))
                try File.copy(from: a, to: b)
                attempt("over") { try File.copy(from: a, to: b, overwrite: false) }
                attempt("itself") { try File.copy(from: a, to: a) }
                attempt("directory") { try File.copy(from: root, to: b) }
                attempt("missing") { try File.copy(from: root.appending("x"), to: b) }
                let metadata = try File.metadata(b)
                print("\(metadata.size) \(metadata.permissions.isReadOnly)")
                try File.setPermissions(a, FilePermissions(mode: 0o644))
                try File.setPermissions(b, FilePermissions(mode: 0o644))
                try File.write(a, "one")
                attempt("rename over") { try File.rename(from: a, to: b, overwrite: false) }
                attempt("rename") { try File.rename(from: a, to: b) }
                print(try File.read(b))
                try File.write(a, "two")
                try File.copy(from: a, to: b)
                print((try File.read(b)) + (File.exists(a) ? " there" : " gone"))
                attempt("remove directory") { try File.remove(root) }
                attempt("remove missing") { try File.remove(root.appending("x")) }
                attempt("remove '..'") { try File.remove("..") }
                attempt("removed") { try File.remove(b) }
                print(File.exists(b))
            """)
        );

    [Fact]
    public void TimesAndLinksAreSet() =>
        Assert.Equal(
            """
            2009-02-13T23:31:30.1234567Z
            1970-01-01T00:16:40Z
            1969-12-31T23:58:20.5Z
            again
            hard link over: exists
            hard link to nothing: not found
            created true
            time of nothing: not found

            """.ReplaceLineEndings("\n"),
            Agree("""
                let a = root.appending("a")
                let b = root.appending("b")
                try File.write(a, "one")
                let then = UtcDateTime { secondsSinceEpoch: 1234567890, nanosecond: 123456700 }
                try File.setModified(a, then)
                try File.setAccessed(a, UtcDateTime.fromUnix(1000))
                print("\((try File.metadata(a)).modified.toString())")
                print("\((try File.metadata(a)).accessed.toString())")
                try File.setModified(a, UtcDateTime { secondsSinceEpoch: -100, nanosecond: 500000000 })
                print("\((try File.open(a).metadata()).modified.toString())")
                try File.createHardLink(at: b, to: a)
                try File.write(b, "again")
                print(try File.read(a))
                attempt("hard link over") { try File.createHardLink(at: b, to: a) }
                attempt("hard link to nothing") { try File.createHardLink(at: root.appending("c"), to: root.appending("x")) }
                print("created \((try File.metadata(a)).kind == FileKind.File)")
                attempt("time of nothing") { try File.setModified(root.appending("x"), then) }
            """)
        );

    [Fact]
    public void DirectoriesAreMadeWalkedAndRemoved() =>
        Assert.Equal(
            """
            no parent: not found
            there: exists
            a a/b a/b/c a/d e
            a/b/c
            list a file: not a directory
            list nothing: not found
            not empty: not empty
            a file: not a directory
            a file's tree: not a directory
            nothing: not found
            '': invalid
            '/': invalid
            '.': invalid
            '..': invalid
            'a/..': invalid
            'a/.': invalid
            copied a/b/c
            removed: done
            false true

            """.ReplaceLineEndings("\n"),
            Agree("""
                attempt("no parent") { try Directory.create(root.appending("a/b")) }
                try Directory.create(root.appending("a/b"), recursive: true)
                attempt("there") { try Directory.create(root.appending("a/b")) }
                try File.write(root.appending("a/b/c"), "c")
                try File.write(root.appending("a/d"), "d")
                try File.write(root.appending("e"), "e")
                var options = WalkOptions()
                options.sorted = true
                var line = ""
                for try entry in try Directory.walk(root, options: options) {
                    line = line + entry.path.relative(to: root)!.components.map { "\($0)" }.joined(separator: "/") + " "
                }
                print(line.trimmed())
                line = ""
                for try entry in try Directory.glob(root, "a/**/?", options: options) {
                    if entry.isFile && entry.depth == 3 {
                        line = line + entry.path.relative(to: root)!.components.map { "\($0)" }.joined(separator: "/") + " "
                    }
                }
                print(line.trimmed())
                attempt("list a file") { let _ = try Directory.list(root.appending("e")) }
                attempt("list nothing") { let _ = try Directory.list(root.appending("x")) }
                attempt("not empty") { try Directory.remove(root.appending("a")) }
                attempt("a file") { try Directory.remove(root.appending("e")) }
                attempt("a file's tree") { try Directory.remove(root.appending("e"), recursive: true) }
                attempt("nothing") { try Directory.remove(root.appending("x"), recursive: true) }
                for path in ["", "/", ".", "..", "a/..", "a/."] {
                    attempt("'\(path)'") { try Directory.remove(path, recursive: true) }
                }
                try Directory.copy(from: root.appending("a"), to: root.appending("z"))
                print("copied \((try File.read(root.appending("z/b/c"))) == "c" ? "a/b/c" : "nothing")")
                attempt("removed") { try Directory.remove(root.appending("a"), recursive: true) }
                print("\(Directory.exists(root.appending("a"))) \(File.exists(root.appending("z/d")))")
            """)
        );

    /// <summary>Links to what is outside a tree are removed, and what they lead to is left, by both.</summary>
    [Fact]
    public void NoLinkIsFollowedOutOfATree() {
        if (OperatingSystem.IsWindows()) {
            // Not every user may make a link.
            return;
        }

        Assert.Equal(
            """
            1
            kind true, leads to ../outside
            a link: not a directory
            a link and a separator: not a directory
            no link: invalid
            removed: done
            false kept deep
            the link itself: done
            kept

            """.ReplaceLineEndings("\n"),
            Agree("""
                try Directory.create(root.appending("outside/deep"), recursive: true)
                try File.write(root.appending("outside/deep/f"), "deep")
                try File.write(root.appending("outside/f"), "kept")
                try Directory.create(root.appending("tree/sub/sub"), recursive: true)
                try File.createSymlink(at: root.appending("tree/link"), to: "../outside")
                try File.createSymlink(at: root.appending("tree/sub/file"), to: root.appending("outside/f"))
                try File.createSymlink(at: root.appending("tree/sub/sub/up"), to: "../../..")
                try File.createSymlink(at: root.appending("tree/sub/sub/root"), to: "/")
                try File.createSymlink(at: root.appending("tree/nowhere"), to: "nowhere")
                try File.createSymlink(at: root.appending("link"), to: root.appending("outside"))
                var links = 0
                for try entry in try Directory.entries(root.appending("tree")) {
                    if entry.isSymlink && entry.name == "link" {
                        links += 1
                    }
                }
                print(links)
                let link = root.appending("tree/link")
                print("kind \((try File.metadata(link, followLinks: false)).isSymlink), leads to \(try File.readLink(link))")
                attempt("a link") { try Directory.remove(link, recursive: true) }
                attempt("a link and a separator") { try Directory.remove("\(link)/", recursive: true) }
                attempt("no link") { let _ = try File.readLink(root.appending("outside/f")) }
                attempt("removed") { try Directory.remove(root.appending("tree"), recursive: true) }
                print("\(Directory.exists(root.appending("tree"))) \(try File.read(root.appending("outside/f"))) \(try File.read(root.appending("outside/deep/f")))")
                attempt("the link itself") { try File.remove(root.appending("link")) }
                print(try File.read(root.appending("outside/f")))
            """)
        );
    }

    [Fact]
    public void TemporaryFilesGoAsTheyAreLetGo() =>
        Assert.Equal(
            "true true\nfalse false\nkept\n1\n",
            Agree("""
                var file = FilePath("")
                var directory = FilePath("")
                do {
                    let a = try Temp.file(in: root)
                    let b = try Temp.directory(in: root)
                    try File.write(b.path.appending("inside"), "x")
                    file = a.path
                    directory = b.path
                    print("\(File.exists(file)) \(Directory.exists(directory))")
                }
                print("\(File.exists(file)) \(Directory.exists(directory))")
                let kept = try Temp.file(in: root)
                try kept.file.write("kept".utf8Bytes())
                try kept.persist(to: root.appending("settings"))
                print(try File.read(root.appending("settings")))
                print(try Directory.list(root).count)
            """)
        );

    [Fact]
    public void ChangesAreHeardOf() =>
        Assert.Equal(
            "created\nremoved\ncancelled\nnot there\n",
            Agree("""
                let path = root.appending("file")
                let watcher = try FileWatcher.watch(root)
                try File.write(path, "x")
                for await event in watcher {
                    if event.path == path && event.kind == .Created {
                        print("created")
                        break
                    }
                }
                try File.remove(path)
                for await event in watcher {
                    if event.path == path && event.kind == .Removed {
                        print("removed")
                        break
                    }
                }
                let waiting = Task.spawn {
                    for await event in watcher {
                        if event.path.fileName == "never" {
                            print("never")
                        }
                    }
                    print("cancelled")
                }
                waiting.cancel()
                await waiting.value
                do {
                    let _ = try FileWatcher.watch(root.appending("missing"))
                } catch IoError.NotFound(_) {
                    print("not there")
                }
            """)
        );
}
