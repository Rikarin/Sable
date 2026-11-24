using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Sable.SIL;

/// <summary>
///     The file system by path, interpreted as the runtime gives it
///     (runtime/fs.c, runtime/watch.c). On POSIX the calls are the C
///     library's own, as the runtime's are, so that both fail the same way;
///     on Windows they are .NET's. Files are watched by .NET's
///     <see cref="FileSystemWatcher" />.
/// </summary>
public sealed partial class SilInterpreter {
    // platform.h's FILE_ERROR_ and FILE_KIND_.
    const long ErrorNotFound = 1, ErrorPermission = 2, ErrorExists = 3, ErrorIsDirectory = 4, ErrorInterrupted = 5, ErrorInvalid = 6,
        ErrorOther = 7, ErrorNotEmpty = 8, ErrorNotDirectory = 9, ErrorCrosses = 10, ErrorUnsupported = 11;

    const long KindFile = 0, KindDirectory = 1, KindLink = 2;

    /// <summary>A file system call failed, by the kind and the system's own code.</summary>
    sealed class FileSystemFailure(long kind, long code) : IOException {
        public long Kind => kind;
        public long Code => code;
    }

    readonly Dictionary<long, IEnumerator<(int Kind, string Name)>> _directories = [];
    readonly Dictionary<long, WatchSource> _watches = [];

    object? FileSystem(string name, object?[] operands) {
        switch (name) {
            case "fs_close_directory":
                if (_directories.Remove((long)operands[0]!, out var closed)) {
                    closed.Dispose();
                }

                return null;

            case "fs_watch_event":
                return operands[0];

            case "fs_watch_take":
                return _watches[(long)operands[0]!].Take() is { } taken ? ByteArray([(byte)taken.Kind, ..Encoding.UTF8.GetBytes(taken.Path)]) : null;

            case "fs_unwatch":
                _watches.Remove((long)operands[0]!);
                CloseSource((long)operands[0]!);
                return null;
        }

        _fileError = 0;
        _fileErrorCode = 0;
        try {
            return FileSystemCall(name, operands);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PlatformNotSupportedException) {
            if (e is FileSystemFailure failure) {
                _fileError = failure.Kind;
                _fileErrorCode = failure.Code;
            } else {
                Failed(e, null);
                _fileError = (e.HResult & 0xFFFF) switch {
                    145 => ErrorNotEmpty,
                    17 when OperatingSystem.IsWindows() => ErrorCrosses,
                    1314 => ErrorPermission,
                    _ => e is NotSupportedException ? ErrorUnsupported : _fileError,
                };
            }

            return name switch {
                "fs_metadata" or "fs_read_link" or "fs_next_entry" => null,
                "fs_open_directory" or "fs_watch" => 0L,
                _ => false,
            };
        }
    }

    object? FileSystemCall(string name, object?[] operands) {
        switch (name) {
            case "fs_metadata": {
                var follow = (bool)operands[1]!;
                return IntArray(Metadata(follow ? PathFor(operands[0], SilAccess.Read) : PathItself(operands[0], SilAccess.Read), follow));
            }

            case "fs_set_permissions": {
                var path = PathFor(operands[0], SilAccess.Write);
                var permissions = (int)(long)operands[1]! & 0xFFF;
                if (!OperatingSystem.IsWindows()) {
                    Succeed(Libc.Chmod(path, (uint)permissions));
                } else if (!Directory.Exists(path)) {
                    var attributes = File.GetAttributes(path) & ~FileAttributes.ReadOnly;
                    File.SetAttributes(path, (permissions & 0x92) == 0 ? attributes | FileAttributes.ReadOnly : attributes);
                }

                return true;
            }

            case "fs_set_time": {
                var path = PathFor(operands[0], SilAccess.Write);
                var accessed = (long)operands[1]! == 1;
                var (seconds, nanoseconds) = ((long)operands[2]!, (long)operands[3]!);
                if (!OperatingSystem.IsWindows()) {
                    long omit = OperatingSystem.IsMacOS() ? -2 : (1 << 30) - 2;
                    long[] times = accessed ? [seconds, nanoseconds, 0, omit] : [0, omit, seconds, nanoseconds];
                    Succeed(Libc.Utimensat(OperatingSystem.IsMacOS() ? -2 : -100, path, times, 0));
                    return true;
                }

                if (seconds is < -11644473600 or > 250000000000) {
                    throw new FileSystemFailure(ErrorInvalid, 87);
                }

                if (!File.Exists(path) && !Directory.Exists(path)) {
                    throw new FileNotFoundException(path);
                }

                var time = DateTime.UnixEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + nanoseconds / 100);
                if (accessed) {
                    File.SetLastAccessTimeUtc(path, time);
                } else {
                    File.SetLastWriteTimeUtc(path, time);
                }

                return true;
            }

            case "fs_make_directory": {
                var path = PathFor(operands[0], SilAccess.Write);
                if (!OperatingSystem.IsWindows()) {
                    Succeed(Libc.Mkdir(path, (uint)(long)operands[1]!));
                } else if (!Win32.CreateDirectory(path, 0)) {
                    throw WindowsFailure(Marshal.GetLastPInvokeError());
                }

                return true;
            }

            case "fs_remove": {
                var path = PathItself(operands[0], SilAccess.Write);
                var directory = (bool)operands[1]!;
                var isDirectory = Metadata(path, follow: false)[1] == KindDirectory;
                if (directory != isDirectory) {
                    throw new FileSystemFailure(directory ? ErrorNotDirectory : ErrorIsDirectory, OperatingSystem.IsWindows() ? 267 : directory ? 20 : 21);
                }

                if (!OperatingSystem.IsWindows()) {
                    Succeed(directory ? Libc.Rmdir(path) : Libc.Unlink(path));
                } else {
                    DeleteOne(path);
                }

                return true;
            }

            case "fs_remove_tree": {
                var path = PathItself(operands[0], SilAccess.Write);
                if (path.Contains('\0')) {
                    throw new FileSystemFailure(ErrorInvalid, 0);
                }

                if (OperatingSystem.IsWindows()) {
                    RemoveTreeOnWindows(path);
                } else {
                    RemoveTree(path);
                }

                return true;
            }

            case "fs_rename": {
                var (from, to, replace) = (PathItself(operands[0], SilAccess.Write), PathItself(operands[1], SilAccess.Write), (bool)operands[2]!);
                if (OperatingSystem.IsWindows()) {
                    if (!Win32.MoveFileEx(from, to, replace ? 1u : 0u)) {
                        throw WindowsFailure(Marshal.GetLastPInvokeError());
                    }

                    return true;
                }

                if (replace) {
                    Succeed(Libc.Rename(from, to));
                    return true;
                }

                int result;
                try {
                    result = OperatingSystem.IsMacOS() ? Libc.RenamexNp(from, to, 4) : Libc.Renameat2(-100, from, -100, to, 1);
                } catch (EntryPointNotFoundException) {
                    result = Libc.Link(from, to);
                    if (result == 0) {
                        result = Libc.Unlink(from);
                    }
                }

                Succeed(result);
                return true;
            }

            case "fs_link": {
                var kind = (long)operands[2]!;
                var path = PathItself(operands[1], SilAccess.Write);
                // A hard link's file may be written through it.
                var target = kind == 0 ? PathItself(operands[0], SilAccess.Write) : Text(operands[0]).Value;
                if (!OperatingSystem.IsWindows()) {
                    Succeed(kind == 0 ? Libc.Linkat(OperatingSystem.IsMacOS() ? -2 : -100, target, OperatingSystem.IsMacOS() ? -2 : -100, path, 0) : Libc.Symlink(target, path));
                } else if (kind == 0) {
                    if (!Win32.CreateHardLink(path, target, 0)) {
                        throw WindowsFailure(Marshal.GetLastPInvokeError());
                    }
                } else if (kind == 1) {
                    File.CreateSymbolicLink(path, target.Replace('/', '\\'));
                } else {
                    Directory.CreateSymbolicLink(path, target.Replace('/', '\\'));
                }

                return true;
            }

            case "fs_read_link": {
                var path = PathItself(operands[0], SilAccess.Read);
                if (Metadata(path, follow: false)[1] != KindLink) {
                    throw new FileSystemFailure(ErrorInvalid, OperatingSystem.IsWindows() ? 4390 : 22);
                }

                return ByteArray(Encoding.UTF8.GetBytes(new FileInfo(path).LinkTarget ?? throw new FileNotFoundException(path)));
            }

            case "fs_open_directory": {
                var path = PathFor(operands[0], SilAccess.Read);
                if (Metadata(path, follow: true)[1] != KindDirectory) {
                    throw new FileSystemFailure(ErrorNotDirectory, OperatingSystem.IsWindows() ? 267 : 20);
                }

                var handle = ++_nextSocket;
                _directories[handle] = (OperatingSystem.IsWindows() ? WindowsEntries(path) : PosixEntries(OpenStream(path))).GetEnumerator();
                return handle;
            }

            case "fs_next_entry": {
                var entries = _directories[(long)operands[0]!];
                return entries.MoveNext() ? ByteArray([(byte)entries.Current.Kind, ..Encoding.UTF8.GetBytes(entries.Current.Name)]) : null;
            }

            case "fs_watch": {
                var path = PathFor(operands[0], SilAccess.Read);
                var watch = new WatchSource(path, Metadata(path, follow: true)[1] == KindDirectory, (bool)operands[1]!, _happened.Set);
                var handle = Opened(watch);
                _watches[handle] = watch;
                return handle;
            }

            default:
                throw new InvalidOperationException($"no file system builtin '{name}'");
        }
    }

    HeapArray? ByteArray(byte[] bytes) => bytes.Length == 0 ? null : Allocate(new HeapArray(new ArrayType(SilType.U8), [..bytes.Select(static b => (object?)(long)b)]));

    HeapArray IntArray(long[] values) => Allocate(new HeapArray(new ArrayType(SilType.Int), [..values.Select(static v => (object?)v)]));

    /// <summary>
    ///     The path as <see cref="PathFor" /> gives it, but for a link that is
    ///     its last part, which is not followed: what is done is done to the link.
    /// </summary>
    string PathItself(object? operand, SilAccess access) {
        var text = Text(operand).Value;
        if (Sandbox is not { } sandbox) {
            return text;
        }

        if (text.Length > 0 && !text.Contains('\0')) {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text, sandbox.CurrentDirectory));
            if (Path.GetDirectoryName(full) is { } directory && new FileInfo(full).LinkTarget is not null) {
                return Path.Combine(sandbox.Check(directory, access), Path.GetFileName(full));
            }
        }

        return sandbox.Check(text, access);
    }

    /// <summary>FileMetadata's ten integers, as runtime/platform.h lays them out.</summary>
    static long[] Metadata(string path, bool follow) {
        if (!OperatingSystem.IsWindows()) {
            Succeed(follow ? Libc.Stat(path, out var status) : Libc.LStat(path, out status));
            return Metadata(status);
        }

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var link = info.LinkTarget is not null;
        if (link && follow) {
            info = info.ResolveLinkTarget(returnFinalTarget: true) ?? throw new FileNotFoundException(path);
            link = false;
        }

        if (!link && !info.Exists) {
            throw new FileNotFoundException(path);
        }

        var directory = (info.Attributes & FileAttributes.Directory) != 0;
        var (modified, accessed, created) = (Parts(info.LastWriteTimeUtc), Parts(info.LastAccessTimeUtc), Parts(info.CreationTimeUtc));
        return [
            !directory && info is FileInfo { Exists: true } file ? file.Length : 0, link ? KindLink : directory ? KindDirectory : KindFile,
            ((info.Attributes & FileAttributes.ReadOnly) != 0 ? 0x124 : 0x1B6) | (directory ? 0x49 : 0),
            modified.Seconds, modified.Nanoseconds, accessed.Seconds, accessed.Nanoseconds, created.Seconds, created.Nanoseconds, 1,
        ];

        static (long Seconds, long Nanoseconds) Parts(DateTime time) {
            var ticks = (time - DateTime.UnixEpoch).Ticks;
            var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var left);
            return left < 0 ? (seconds - 1, (left + TimeSpan.TicksPerSecond) * 100) : (seconds, left * 100);
        }
    }

    /// <summary>An open file's.</summary>
    static long[] Metadata(FileStream stream) {
        if (OperatingSystem.IsWindows()) {
            var metadata = Metadata(stream.Name, follow: true);
            metadata[0] = stream.Length;
            return metadata;
        }

        Succeed(Libc.FStat(stream.SafeFileHandle, out var status));
        return Metadata(status);
    }

    static long[] Metadata(FileStatus status) {
        long kind = (status.Mode & 0xF000) switch { 0x8000 => 0, 0x4000 => 1, 0xA000 => 2, 0x1000 => 3, 0xC000 => 4, 0x2000 => 5, 0x6000 => 6, _ => 7 };
        var born = (status.Flags & 1) != 0;
        return [
            status.Size, kind, status.Mode & 0xFFF, status.MTime, status.MTimeNsec, status.ATime, status.ATimeNsec,
            born ? status.BirthTime : 0, born ? status.BirthTimeNsec : 0, born ? 1 : 0,
        ];
    }

    static FileSystemFailure PosixFailure(int code) => new(
        code switch {
            2 or 20 => ErrorNotFound,
            1 or 13 => ErrorPermission,
            17 => ErrorExists,
            21 => ErrorIsDirectory,
            4 => ErrorInterrupted,
            22 or 9 => ErrorInvalid,
            18 => ErrorCrosses,
            _ when OperatingSystem.IsMacOS() => code switch { 63 => ErrorInvalid, 66 => ErrorNotEmpty, 45 or 78 => ErrorUnsupported, _ => ErrorOther },
            36 => ErrorInvalid,
            39 => ErrorNotEmpty,
            38 or 95 => ErrorUnsupported,
            _ => ErrorOther,
        },
        code
    );

    static FileSystemFailure WindowsFailure(int code) => new(
        code switch {
            2 or 3 or 123 => ErrorNotFound,
            5 or 32 or 33 or 1314 => ErrorPermission,
            80 or 183 => ErrorExists,
            145 => ErrorNotEmpty,
            17 => ErrorCrosses,
            1 or 50 => ErrorUnsupported,
            6 or 87 or 206 => ErrorInvalid,
            _ => ErrorOther,
        },
        code
    );

    static void Succeed(int result) {
        if (result < 0) {
            throw PosixFailure(Marshal.GetLastPInvokeError());
        }
    }

    // ---- Directories' entries ---------------------------------------------------

    static nint OpenStream(string path) {
        var stream = Libc.Opendir(path);
        return stream != 0 ? stream : throw PosixFailure(Marshal.GetLastPInvokeError());
    }

    /// <summary>The entries of a directory's stream, '.' and '..' left out, its kinds platform.h's (255: it does not say); then closed.</summary>
    static IEnumerable<(int Kind, string Name)> PosixEntries(nint stream) {
        try {
            while (true) {
                Marshal.SetLastSystemError(0);
                var entry = Libc.Readdir(stream);
                if (entry == 0) {
                    var code = Marshal.GetLastPInvokeError();
                    if (code != 0) {
                        throw PosixFailure(code);
                    }

                    yield break;
                }

                // struct dirent's d_type and d_name: past its number, its
                // offset and its lengths, of which macOS has two.
                var at = OperatingSystem.IsMacOS() ? 20 : 18;
                var name = Marshal.PtrToStringUTF8(entry + at + 1)!;
                if (name is not ("." or "..")) {
                    yield return (Marshal.ReadByte(entry + at) switch { 8 => 0, 4 => 1, 10 => 2, 1 => 3, 12 => 4, 2 => 5, 6 => 6, _ => 255 }, name);
                }
            }
        } finally {
            Libc.Closedir(stream);
        }
    }

    static IEnumerable<(int Kind, string Name)> WindowsEntries(string path) =>
        new DirectoryInfo(path).EnumerateFileSystemInfos().Select(static e =>
            (e.LinkTarget is not null ? (int)KindLink : (e.Attributes & FileAttributes.Directory) != 0 ? (int)KindDirectory : (int)KindFile, e.Name));

    // ---- Removing a tree --------------------------------------------------------

    static int OpenDirectory => OperatingSystem.IsMacOS() ? 0x100000 | 0x100 | 0x1000000 | 0x4
        : RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x4000 | 0x8000 | 0x80000 | 0x800
        : 0x10000 | 0x20000 | 0x80000 | 0x800;

    /// <summary>
    ///     The directory and all in it removed as the runtime removes it
    ///     (runtime/platform_fs_posix.c): by descriptors, no link followed.
    ///     A descriptor is held for each level, here.
    /// </summary>
    static void RemoveTree(string path) {
        var named = path.TrimEnd('/');
        var name = named[(named.LastIndexOf('/') + 1)..];
        if (name is "" or "." or "..") {
            throw new FileSystemFailure(ErrorInvalid, 22);
        }

        if (Libc.LStat(named, out var tree) == 0 && Libc.Stat("/", out var root) == 0 && tree.Dev == root.Dev && tree.Ino == root.Ino) {
            throw new FileSystemFailure(ErrorInvalid, 22);
        }

        var above = Libc.Openat(OperatingSystem.IsMacOS() ? -2 : -100, name.Length == named.Length ? "." : named[..^name.Length], OpenDirectory & ~(OperatingSystem.IsMacOS() ? 0x100 : RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000));
        Succeed(above);
        try {
            RemoveDirectoryIn(above, name, 256);
        } catch (FileSystemFailure) when (Libc.LStat(named, out var status) == 0 && (status.Mode & 0xF000) != 0x4000) {
            throw new FileSystemFailure(ErrorNotDirectory, 20);
        } finally {
            Libc.Close(above);
        }
    }

    static void RemoveDirectoryIn(int above, string name, int retries) {
        for (;; retries--) {
            var directory = Libc.Openat(above, name, OpenDirectory);
            Succeed(directory);
            try {
                var stream = Libc.Openat(directory, ".", OpenDirectory);
                Succeed(stream);
                var entries = Libc.Fdopendir(stream);
                if (entries == 0) {
                    Libc.Close(stream);
                    throw PosixFailure(Marshal.GetLastPInvokeError());
                }

                foreach (var (kind, inside) in PosixEntries(entries).ToList()) {
                    if (kind != KindDirectory && Libc.Unlinkat(directory, inside, 0) == 0) {
                        continue;
                    }

                    var code = kind != KindDirectory ? Marshal.GetLastPInvokeError() : 0;
                    try {
                        // Not one there as it is opened, unlinked in its turn.
                        if (code is 0 or 1 or 21) {
                            RemoveDirectoryIn(directory, inside, retries);
                        } else if (code != 2) {
                            throw PosixFailure(code);
                        }
                    } catch (FileSystemFailure failure) when (failure.Code is 2) {
                    } catch (FileSystemFailure failure) when (failure.Code is 20 or 62 or 40) {
                        // It was a directory, or looked one: it is not.
                        if (code != 0) {
                            throw PosixFailure(code);
                        }

                        if (Libc.Unlinkat(directory, inside, 0) != 0 && Marshal.GetLastPInvokeError() != 2) {
                            throw;
                        }
                    }
                }
            } finally {
                Libc.Close(directory);
            }

            if (Libc.Unlinkat(above, name, OperatingSystem.IsMacOS() ? 0x80 : 0x200) == 0 || Marshal.GetLastPInvokeError() == 2) {
                return;
            }

            // Written to as it was emptied: emptied again, though not for ever.
            var left = Marshal.GetLastPInvokeError();
            if (left is not (39 or 66 or 17) || retries <= 0) {
                throw PosixFailure(left);
            }
        }
    }

    /// <summary>
    ///     The same by .NET's calls: a reparse point is removed itself, and
    ///     never gone into. Not against a directory swapped for a link
    ///     meanwhile, as the runtime's is.
    /// </summary>
    static void RemoveTreeOnWindows(string path) {
        var named = path.TrimEnd('\\', '/');
        var last = named.Length == 0 ? '\0' : named[^1];
        if (named.Length == 0 || last is ':' or '.' or ' ' || Path.GetFullPath(named) == Path.GetPathRoot(Path.GetFullPath(named))) {
            throw new FileSystemFailure(ErrorInvalid, 87);
        }

        var attributes = File.GetAttributes(named);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) {
            throw new FileSystemFailure(ErrorNotDirectory, 267);
        }

        Empty(new DirectoryInfo(named));

        static void Empty(DirectoryInfo directory) {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList()) {
                try {
                    if (entry is DirectoryInfo inside && (entry.Attributes & FileAttributes.ReparsePoint) == 0) {
                        Empty(inside);
                    } else {
                        DeleteOne(entry.FullName);
                    }
                } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
                    // Gone already.
                }
            }

            DeleteOne(directory.FullName);
        }
    }

    /// <summary>A file, a link or an empty directory deleted, read-only or not.</summary>
    static void DeleteOne(string path) {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0 && (attributes & FileAttributes.ReparsePoint) == 0) {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        if ((attributes & FileAttributes.Directory) != 0) {
            Directory.Delete(path, recursive: false);
        } else {
            File.Delete(path);
        }
    }

    /// <summary>A copy as runtime/platform_fs_posix.c makes it: a file there written over where it is, and never from itself.</summary>
    static void CopyFile(string from, string to, bool replace) {
        var source = Metadata(from, follow: true);
        if (source[1] != KindFile) {
            throw new FileSystemFailure(source[1] == KindDirectory ? ErrorIsDirectory : ErrorInvalid, OperatingSystem.IsWindows() ? 267 : 21);
        }

        if (OperatingSystem.IsWindows()) {
            if (replace && File.Exists(to) && Path.GetFullPath(from).Equals(Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) {
                throw new FileSystemFailure(ErrorInvalid, 32);
            }

            File.Copy(from, to, replace);
            return;
        }

        if (Directory.Exists(to)) {
            throw new FileSystemFailure(replace ? ErrorIsDirectory : ErrorExists, replace ? 21 : 17);
        }

        if (!replace && Libc.LStat(to, out _) == 0) {
            throw new FileSystemFailure(ErrorExists, 17);
        }

        if (Libc.Stat(from, out var one) == 0 && Libc.Stat(to, out var other) == 0 && one.Dev == other.Dev && one.Ino == other.Ino) {
            throw new FileSystemFailure(ErrorInvalid, 22);
        }

        using (var read = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var write = new FileStream(to, replace ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) {
            read.CopyTo(write);
        }

        Succeed(Libc.Chmod(to, (uint)source[2] & 0x1FF));
    }

    // ---- Watching ---------------------------------------------------------------

    /// <summary>What happened to a path, as runtime/platform.h's WATCH_ say, and where in it.</summary>
    sealed class WatchSource : InterpretedSource {
        readonly ConcurrentQueue<(int Kind, string Path)> _events = new();
        readonly FileSystemWatcher _watcher;
        readonly string _root;
        readonly bool _directory;
        readonly Action _happened;

        public WatchSource(string path, bool directory, bool recursive, Action happened) {
            _directory = directory;
            _happened = happened;
            _root = Path.GetFullPath(directory ? path : Path.GetDirectoryName(Path.GetFullPath(path))!);
            _watcher = new(_root) {
                IncludeSubdirectories = directory && recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 65536,
            };
            if (!directory) {
                _watcher.Filters.Add(Path.GetFileName(path));
            }

            _watcher.Created += (_, e) => Add(1, e.FullPath);
            _watcher.Deleted += (_, e) => Add(3, e.FullPath);
            _watcher.Changed += (_, e) => {
                if (!Directory.Exists(e.FullPath)) {
                    Add(2, e.FullPath);
                }
            };
            _watcher.Renamed += (_, e) => {
                Add(3, e.OldFullPath);
                Add(1, e.FullPath);
            };
            _watcher.Error += (_, _) => Add(Path.Exists(_root) ? 4 : 3, _root);
            _watcher.EnableRaisingEvents = true;
        }

        void Add(int kind, string path) {
            var relative = _directory ? Path.GetRelativePath(_root, path) : ".";
            _events.Enqueue((kind, relative == "." ? "" : relative));
            _happened();
        }

        public (int Kind, string Path)? Take() => _events.TryDequeue(out var taken) ? taken : null;

        public override bool Background => true;
        public override bool Ready(bool writable) => !_events.IsEmpty;
        public override void Close() => _watcher.Dispose();
    }

    // ---- The system's calls -------------------------------------------------------

    /// <summary>What .NET's own libSystem.Native gives of a file, laid out the same on every POSIX system.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FileStatus {
        public int Flags;
        public int Mode;
        public uint Uid, Gid;
        public long Size, ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec, BirthTime, BirthTimeNsec, Dev, RDev, Ino;
        public uint UserFlags;
        public long Spare0, Spare1, Spare2, Spare3;
    }

    static class Libc {
        [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true)]
        public static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus status);

        [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
        public static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus status);

        [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
        public static extern int FStat(SafeHandle file, out FileStatus status);

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        public static extern int Chmod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        [DllImport("libc", EntryPoint = "utimensat", SetLastError = true)]
        public static extern int Utimensat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, long[] times, int flags);

        [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
        public static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
        public static extern int Unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport("libc", EntryPoint = "rmdir", SetLastError = true)]
        public static extern int Rmdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
        public static extern int Rename([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

        [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
        public static extern int RenamexNp([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
        public static extern int Renameat2(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        public static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

        [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
        public static extern int Linkat(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, int flags);

        [DllImport("libc", EntryPoint = "symlink", SetLastError = true)]
        public static extern int Symlink([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        // Variadic, its mode left out: the fixed arguments are passed as any are.
        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        public static extern int Openat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        public static extern int Unlinkat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int descriptor);

        [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
        public static extern int Closedir(nint stream);

        // On an x64 Mac the 64-bit inodes' calls have names of their own.
        static readonly bool Inode64 = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

        public static nint Opendir(string path) => Inode64 ? Opendir64(path) : OpendirPlain(path);
        public static nint Fdopendir(int descriptor) => Inode64 ? Fdopendir64(descriptor) : FdopendirPlain(descriptor);
        public static nint Readdir(nint stream) => Inode64 ? Readdir64(stream) : ReaddirPlain(stream);

        [DllImport("libc", EntryPoint = "opendir", SetLastError = true)]
        static extern nint OpendirPlain([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport("libc", EntryPoint = "opendir$INODE64", SetLastError = true)]
        static extern nint Opendir64([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
        static extern nint FdopendirPlain(int descriptor);

        [DllImport("libc", EntryPoint = "fdopendir$INODE64", SetLastError = true)]
        static extern nint Fdopendir64(int descriptor);

        [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
        static extern nint ReaddirPlain(nint stream);

        [DllImport("libc", EntryPoint = "readdir$INODE64", SetLastError = true)]
        static extern nint Readdir64(nint stream);
    }

    static class Win32 {
        [DllImport("kernel32", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateDirectory(string path, nint security);

        [DllImport("kernel32", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileEx(string from, string to, uint flags);

        [DllImport("kernel32", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLink(string path, string target, nint security);
    }
}
