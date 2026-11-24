namespace Sable.SIL;

/// <summary>
///     Files by handle, interpreted as the runtime gives them (runtime/io.c,
///     "File handles"): each a .NET <see cref="FileStream" /> under a handle
///     of its own, the standard streams handles 0, 1 and 2. A call that fails
///     gives -1, nothing or false, and <c>file_error</c> then says why by
///     IoError's kinds (platform.h's FILE_ERROR_), <c>file_error_code</c> by
///     the exception's HRESULT. Standard output and error go to
///     <see cref="Output" /> and <see cref="ErrorOutput" />, as everything the
///     program prints does here; standard input is at its end.
/// </summary>
public sealed partial class SilInterpreter {
    readonly Dictionary<long, FileStream> _files = [];
    long _nextFile = 3;
    long _fileError;
    long _fileErrorCode;

    // platform.h's FILE_ flags.
    const long FileRead = 1, FileWrite = 2, FileAppend = 4, FileCreate = 8, FileCreateNew = 16, FileTruncate = 32;

    object? Files(string name, object?[] operands) {
        if (name is "file_error") {
            return _fileError;
        }

        if (name is "file_error_code") {
            return _fileErrorCode;
        }

        _fileError = 0;
        _fileErrorCode = 0;
        try {
            return FileCall(name, operands);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException) {
            Failed(e, name is "file_open" or "file_copy" or "file_canonical" ? PathFor(operands[0], SilAccess.Read) : null);
            return name switch {
                "file_read" => null,
                "file_canonical" => Allocate(new HeapString("")),
                "file_sync" or "file_set_length" or "file_close" or "file_copy" => false,
                _ => -1L,
            };
        }
    }

    // What went wrong, by IoError's kinds: as errno would say it natively.
    void Failed(Exception e, string? path) {
        _fileErrorCode = e.HResult;
        _fileError = e switch {
            FileNotFoundException or DirectoryNotFoundException => 1,
            UnauthorizedAccessException when path is not null && Directory.Exists(path) => 4,
            UnauthorizedAccessException => 2,
            // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS, EEXIST.
            IOException when (e.HResult & 0xFFFF) is 80 or 183 or 17 => 3,
            ArgumentException or ObjectDisposedException => 6,
            _ => 7,
        };
    }

    object? FileCall(string name, object?[] operands) {
        switch (name) {
            case "file_open": {
                var path = PathFor(operands[0], SilAccess.Read);
                var flags = (long)operands[1]!;
                if (Directory.Exists(path)) {
                    _fileError = 4;
                    return -1L;
                }

                var writes = (flags & (FileWrite | FileAppend)) != 0;
                if (writes) {
                    PathFor(operands[0], SilAccess.Write);
                }

                var mode = (flags & FileCreateNew) != 0 ? FileMode.CreateNew
                    : (flags & FileCreate) != 0 && (flags & FileTruncate) != 0 ? FileMode.Create
                    : (flags & FileCreate) != 0 ? FileMode.OpenOrCreate
                    : (flags & FileTruncate) != 0 ? FileMode.Truncate
                    : FileMode.Open;
                var access = (flags & FileRead) != 0 && writes ? FileAccess.ReadWrite : writes ? FileAccess.Write : FileAccess.Read;
                var stream = new FileStream(path, mode, access, FileShare.ReadWrite | FileShare.Delete);
                if ((flags & FileAppend) != 0) {
                    stream.Seek(0, SeekOrigin.End);
                }

                var handle = _nextFile++;
                _files[handle] = stream;
                _appending[handle] = (flags & FileAppend) != 0;
                return handle;
            }

            case "file_read": {
                var handle = (long)operands[0]!;
                var most = (int)Math.Clamp((long)operands[1]!, 0, 1 << 24);
                var offset = (long)operands[2]!;
                if (handle is 0 or 1 or 2) {
                    return null;
                }

                var stream = FileOf(handle);
                var buffer = new byte[most];
                var got = offset < 0 ? stream.Read(buffer) : RandomAccess.Read(stream.SafeFileHandle, buffer, offset);
                return got == 0 ? null : Allocate(new HeapArray(new ArrayType(SilType.U8), [..buffer.Take(got).Select(static b => (object?)(long)b)]));
            }

            case "file_write": {
                var handle = (long)operands[0]!;
                var bytes = BytesOf(operands[1]);
                var offset = (long)operands[2]!;
                if (handle is 1 or 2) {
                    (handle == 1 ? Output : ErrorOutput).Append(System.Text.Encoding.UTF8.GetString(bytes));
                    return (long)bytes.Length;
                }

                var stream = FileOf(handle);
                if (offset < 0 || _appending.GetValueOrDefault(handle)) {
                    if (_appending.GetValueOrDefault(handle)) {
                        stream.Seek(0, SeekOrigin.End);
                    }

                    stream.Write(bytes);
                } else {
                    RandomAccess.Write(stream.SafeFileHandle, bytes, offset);
                }

                stream.Flush();
                return (long)bytes.Length;
            }

            case "file_seek": {
                var origin = (long)operands[2]! switch {
                    0 => SeekOrigin.Begin,
                    1 => SeekOrigin.Current,
                    _ => SeekOrigin.End,
                };
                return FileOf((long)operands[0]!).Seek((long)operands[1]!, origin);
            }

            case "file_sync":
                FileOf((long)operands[0]!).Flush(flushToDisk: true);
                return true;

            case "file_set_length":
                FileOf((long)operands[0]!).SetLength((long)operands[1]!);
                return true;

            case "file_metadata": {
                var stream = FileOf((long)operands[0]!);
                return (long)operands[1]! switch {
                    0 => stream.Length,
                    1 => (File.GetLastWriteTimeUtc(stream.Name) - DateTime.UnixEpoch).Ticks * 100,
                    _ => 0L,
                };
            }

            case "file_close": {
                var handle = (long)operands[0]!;
                if (_files.Remove(handle, out var stream)) {
                    stream.Dispose();
                    _appending.Remove(handle);
                    return true;
                }

                _fileError = 6;
                return false;
            }

            case "file_canonical": {
                var path = PathFor(operands[0], SilAccess.Read);
                if (!File.Exists(path) && !Directory.Exists(path)) {
                    throw new FileNotFoundException(path);
                }

                return Allocate(new HeapString(Canonical(Path.GetFullPath(path))));
            }

            case "file_standard":
                return operands[0];

            case "file_copy": {
                var source = PathFor(operands[0], SilAccess.Read);
                var target = PathFor(operands[1], SilAccess.Write);
                File.Copy(source, target, overwrite: true);
                return true;
            }

            default:
                throw new InvalidOperationException($"no file builtin '{name}'");
        }
    }

    readonly Dictionary<long, bool> _appending = [];

    // The full path with every link on it resolved, a component at a time:
    // realpath, which .NET has no call for.
    static string Canonical(string full) {
        var root = Path.GetPathRoot(full)!;
        var resolved = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
            var next = Path.Combine(resolved, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            // A link's target may itself go through links: resolved again.
            resolved = info.LinkTarget is null ? next : Canonical(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next);
        }

        return resolved;
    }

    FileStream FileOf(long handle) =>
        _files.TryGetValue(handle, out var stream) ? stream : throw new ObjectDisposedException($"file handle {handle}");
}
