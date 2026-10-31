using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>File descriptors, open file descriptions, pipes, poll and select.</summary>
public static unsafe partial class HostSyscalls
{
    private enum FileKind { Regular, Directory, Pipe, Console, Device }

    /// <summary>An open file description, shared by the descriptors dup makes: offset, status flags and the stream.</summary>
    private sealed class OpenFile(FileKind kind, Stream? stream, string? path, int flags)
    {
        internal readonly FileKind Kind = kind;
        internal readonly Stream? Stream = stream;
        internal readonly string? Path = path;
        internal int Flags = flags;                 // access mode and status flags (O_APPEND, O_NONBLOCK)
        internal Pipe? Pipe;                        // FileKind.Pipe: the buffer, and which end this is
        internal bool PipeWriter;
        internal int Console = -1;                  // FileKind.Console: 0, 1 or 2
        internal List<(string Name, byte Type)>? Entries;
        internal int EntryIndex;
        internal int References = 1;
        internal bool CanRead => (Flags & O_PATH) == 0 && (Flags & O_ACCMODE) != O_WRONLY;
        internal bool CanWrite => (Flags & O_PATH) == 0 && (Flags & O_ACCMODE) != O_RDONLY;
        internal bool NonBlocking => (Flags & O_NONBLOCK) != 0;
        internal bool Terminal => Console switch
        {
            0 => !System.Console.IsInputRedirected,
            1 => !System.Console.IsOutputRedirected,
            2 => !System.Console.IsErrorRedirected,
            _ => false
        };
    }

    private sealed record Descriptor(OpenFile File, bool CloseOnExec);

    private static readonly object gate = new();
    private static readonly Dictionary<int, Descriptor> descriptors = new()
    {
        [0] = new(StandardStream(0), false),
        [1] = new(StandardStream(1), false),
        [2] = new(StandardStream(2), false),
    };

    private static OpenFile StandardStream(int number) => new(FileKind.Console, number switch
    {
        0 => Console.OpenStandardInput(),
        1 => Console.OpenStandardOutput(),
        _ => Console.OpenStandardError()
    }, null, number == 0 ? O_RDONLY : O_WRONLY)
    { Console = number };

    private static OpenFile? Lookup(int descriptor)
    {
        lock (gate) return descriptors.GetValueOrDefault(descriptor)?.File;
    }

    /// <summary>The lowest free descriptor at or above <paramref name="minimum"/>, or -EMFILE beyond RLIMIT_NOFILE.</summary>
    private static int Install(OpenFile file, bool closeOnExec, int minimum = 0)
    {
        ulong limit = SoftLimit(RLIMIT_NOFILE);
        lock (gate)
        {
            int descriptor = minimum;
            while (descriptors.ContainsKey(descriptor)) descriptor++;
            if ((ulong)descriptor >= limit)
            {
                if (file.References == 1 && file.Kind != FileKind.Console) file.Stream?.Dispose();
                return -EMFILE;
            }
            descriptors[descriptor] = new Descriptor(file, closeOnExec);
            return descriptor;
        }
    }

    private static OpenFile Retain(OpenFile file)
    {
        lock (gate) file.References++;
        file.Pipe?.Retain(file.PipeWriter);
        return file;
    }

    private static void Release(OpenFile file)
    {
        bool last;
        lock (gate) last = --file.References == 0;
        file.Pipe?.Release(file.PipeWriter);
        if (last && file.Kind != FileKind.Console) file.Stream?.Dispose();
    }

    private static long Read(int descriptor, nint buffer, long count)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null || !file.CanRead) return -EBADF;
        if (file.Kind == FileKind.Directory) return -EISDIR;
        if (count < 0) return -EINVAL;
        var target = new Span<byte>((void*)buffer, (int)Math.Min(count, int.MaxValue));
        if (file.Pipe is Pipe pipe) return pipe.Read(target, file.NonBlocking);
        return file.Stream!.Read(target);
    }

    private static long Write(int descriptor, nint buffer, long count)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null || !file.CanWrite) return -EBADF;
        if (count < 0) return -EINVAL;
        var source = new ReadOnlySpan<byte>((void*)buffer, (int)Math.Min(count, int.MaxValue));
        if (file.Pipe is Pipe pipe)
        {
            long written = pipe.Write(source, file.NonBlocking);
            if (written == -EPIPE) Raise(SIGPIPE);
            return written;
        }
        Stream stream = file.Stream!;
        if ((file.Flags & O_APPEND) != 0 && stream.CanSeek) stream.Seek(0, SeekOrigin.End);
        stream.Write(source);
        if (file.Kind == FileKind.Console) stream.Flush();
        return source.Length;
    }

    /// <summary>pread64: regular files only; the descriptor's offset is not changed.</summary>
    private static long ReadAt(int descriptor, nint buffer, long count, long offset)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null || !file.CanRead) return -EBADF;
        if (file.Kind == FileKind.Directory) return -EISDIR;
        if (file.Stream is not { CanSeek: true } stream) return -ESPIPE;
        if (offset < 0 || count < 0) return -EINVAL;
        var target = new Span<byte>((void*)buffer, (int)Math.Min(count, int.MaxValue));
        if (stream is FileStream fileStream) return RandomAccess.Read(fileStream.SafeFileHandle, target, offset);
        lock (stream)
        {
            long position = stream.Position;
            stream.Position = offset;
            int done = stream.Read(target);
            stream.Position = position;
            return done;
        }
    }

    /// <summary>pwrite64: as Linux, a descriptor opened with O_APPEND appends whatever the offset.</summary>
    private static long WriteAt(int descriptor, nint buffer, long count, long offset)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null || !file.CanWrite) return -EBADF;
        if (file.Stream is not FileStream stream) return file.Pipe is not null || file.Kind == FileKind.Console ? -ESPIPE : -EINVAL;
        if (offset < 0 || count < 0) return -EINVAL;
        var source = new ReadOnlySpan<byte>((void*)buffer, (int)Math.Min(count, int.MaxValue));
        RandomAccess.Write(stream.SafeFileHandle, source, (file.Flags & O_APPEND) != 0 ? RandomAccess.GetLength(stream.SafeFileHandle) : offset);
        return source.Length;
    }

    /// <summary>
    /// sendfile and copy_file_range: copies from a regular file through a buffer, from its position or from *inputOffset
    /// (which then advances instead), to the output's position or *outputOffset, at most 0x7ffff000 bytes per call as in Linux.
    /// </summary>
    private static long CopyRange(int input, nint inputOffset, int output, nint outputOffset, long count)
    {
        OpenFile? source = Lookup(input), destination = Lookup(output);
        if (source is null || !source.CanRead || destination is null || !destination.CanWrite) return -EBADF;
        if (source.Kind != FileKind.Regular || count < 0) return -EINVAL;
        if (outputOffset != 0 && destination.Kind != FileKind.Regular) return -EINVAL;
        count = Math.Min(count, 0x7ffff000);
        const int size = 65536;
        byte* buffer = (byte*)NativeMemory.Alloc(size);
        long total = 0;
        try
        {
            while (total < count)
            {
                long chunk = Math.Min(size, count - total);
                long read = inputOffset != 0 ? ReadAt(input, (nint)buffer, chunk, *(long*)inputOffset + total) : Read(input, (nint)buffer, chunk);
                if (read <= 0)
                {
                    if (total == 0 && read < 0) return read;
                    break;
                }
                long written = outputOffset != 0 ? WriteAt(output, (nint)buffer, read, *(long*)outputOffset + total) : Write(output, (nint)buffer, read);
                if (written <= 0)
                {
                    if (total == 0) return written;
                    break;
                }
                total += written;
                if (read < chunk || written < read) break;
            }
        }
        finally { NativeMemory.Free(buffer); }
        if (inputOffset != 0) *(long*)inputOffset += total;
        if (outputOffset != 0) *(long*)outputOffset += total;
        return total;
    }

    private static long Vector(int descriptor, nint vectors, int count, bool read, long offset)
    {
        if (count is < 0 or > 1024) return -EINVAL;
        long total = 0;
        for (int index = 0; index < count; index++)
        {
            nint start = ((nint*)vectors)[index * 2];
            long length = ((long*)vectors)[index * 2 + 1];
            if (length == 0) continue;
            long done = offset < 0
                ? read ? Read(descriptor, start, length) : Write(descriptor, start, length)
                : read ? ReadAt(descriptor, start, length, offset + total) : WriteAt(descriptor, start, length, offset + total);
            if (done < 0) return total > 0 ? total : done;
            total += done;
            if (done < length) break;
        }
        return total;
    }

    private static long Seek(int descriptor, long offset, int whence)
    {
        const int SeekSet = 0, SeekCurrent = 1, SeekEnd = 2, SeekData = 3, SeekHole = 4;
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Kind == FileKind.Directory)
        {
            // Directory offsets are entry positions, as getdents64 reports them in d_off (rewinddir, seekdir, telldir).
            long entry = whence switch { SeekSet => offset, SeekCurrent => file.EntryIndex + offset, _ => -1 };
            if (entry < 0) return -EINVAL;
            if (entry == 0) file.Entries = null;
            file.EntryIndex = (int)Math.Min(entry, int.MaxValue);
            return entry;
        }
        using FileStream? redirected = RedirectedFile(file);
        if ((redirected ?? file.Stream) is not { CanSeek: true } stream) return -ESPIPE;
        lock (stream)
        {
            long length = stream.Length;
            long position = whence switch
            {
                SeekSet => offset,
                SeekCurrent => stream.Position + offset,
                SeekEnd => length + offset,
                SeekData => offset < length ? offset : -2,
                SeekHole => offset < length ? length : -2,
                _ => -1
            };
            if (position == -2) return -ENXIO;
            if (position < 0) return -EINVAL;
            position = stream.Seek(position, SeekOrigin.Begin);
            // FileStream keeps its own offset; reading SafeFileHandle moves the descriptor's (lseek), as reads use it.
            if (redirected is not null) _ = redirected.SafeFileHandle;
            return position;
        }
    }

    /// <summary>
    /// A standard stream the shell redirected from or to a regular file, as a FileStream over the descriptor, or null.
    /// Reads and writes stay on the console stream, whose read(2) and write(2) move the descriptor's offset as natively
    /// (other commands of the shell share it); the FileStream, created per call from that offset, only seeks and sizes
    /// it. Windows standard handles are not descriptors 0 to 2, so there they stay streams.
    /// </summary>
    private static FileStream? RedirectedFile(OpenFile file)
    {
        if (file.Kind != FileKind.Console || file.Terminal || OperatingSystem.IsWindows()) return null;
        try
        {
            var stream = new FileStream(new SafeFileHandle(file.Console, false), file.Console == 0 ? FileAccess.Read : FileAccess.Write, 0);
            if (stream.CanSeek) return stream;
            stream.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private static long Close(int descriptor)
    {
        Descriptor? closed;
        lock (gate)
            if (!descriptors.Remove(descriptor, out closed)) return -EBADF;
        Release(closed.File);
        return 0;
    }

    private static long Duplicate(int descriptor, int target, int flags)
    {
        if ((flags & ~O_CLOEXEC) != 0) return -EINVAL;
        OpenFile? replaced = null;
        int result;
        lock (gate)
        {
            if (!descriptors.TryGetValue(descriptor, out Descriptor? source)) return -EBADF;
            ulong limit = SoftLimit(RLIMIT_NOFILE);
            if (target >= 0 && (ulong)target >= limit) return -EBADF;
            if (target < 0)
            {
                target = 0;
                while (descriptors.ContainsKey(target)) target++;
                if ((ulong)target >= limit) return -EMFILE;
            }
            if (descriptors.Remove(target, out Descriptor? old)) replaced = old.File;
            Retain(source.File);
            descriptors[target] = new Descriptor(source.File, (flags & O_CLOEXEC) != 0);
            result = target;
        }
        if (replaced is not null) Release(replaced);
        return result;
    }

    private static long FileControl(int descriptor, int command, long argument)
    {
        const int SetPipeSize = 1031, GetPipeSize = 1032, Unlocked = 2;
        Descriptor? entry;
        lock (gate) entry = descriptors.GetValueOrDefault(descriptor);
        if (entry is null) return -EBADF;
        OpenFile file = entry.File;
        switch (command)
        {
            case F_DUPFD or F_DUPFD_CLOEXEC:
                if (argument is < 0 or > int.MaxValue) return -EINVAL;
                int duplicate = Install(Retain(file), command == F_DUPFD_CLOEXEC, (int)argument);
                if (duplicate < 0) Release(file);
                return duplicate;
            case F_GETFD:
                return entry.CloseOnExec ? FD_CLOEXEC : 0;
            case F_SETFD:
                lock (gate)
                    if (descriptors.ContainsKey(descriptor)) descriptors[descriptor] = entry with { CloseOnExec = (argument & FD_CLOEXEC) != 0 };
                return 0;
            case F_GETFL:
                return file.Flags & ~O_CLOEXEC;
            case F_SETFL:
                const int changeable = O_APPEND | O_NONBLOCK;
                file.Flags = file.Flags & ~changeable | (int)argument & changeable;
                return 0;
            case F_GETLK or F_OFD_GETLK:
                *(short*)argument = Unlocked;                        // struct flock l_type: one process holds every lock
                return 0;
            case F_SETLK or F_SETLKW or F_OFD_SETLK or F_OFD_SETLKW:
                return 0;
            case GetPipeSize:
                return file.Pipe is null ? -EBADF : Pipe.Capacity;
            case SetPipeSize:
                return file.Pipe is null ? -EBADF : Pipe.Capacity;
            default:
                return -EINVAL;
        }
    }

    private static long Flush(int descriptor)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Stream is FileStream stream) stream.Flush(true);
        else if (file.Kind is FileKind.Pipe or FileKind.Console) return -EINVAL;
        return 0;
    }

    private static long Truncate(int descriptor, long length)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Stream is not FileStream stream || !file.CanWrite || length < 0) return -EINVAL;
        lock (stream)
        {
            long position = stream.Position;
            stream.SetLength(length);
            stream.Position = position;
        }
        return 0;
    }

    /// <summary>fallocate mode 0: makes the file at least offset + length bytes long.</summary>
    private static long Allocate(int descriptor, int mode, long offset, long length)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null || !file.CanWrite) return -EBADF;
        if (file.Stream is not FileStream stream) return -ENODEV;
        if (mode != 0) return -EOPNOTSUPP;
        if (offset < 0 || length <= 0) return -EINVAL;
        lock (stream)
            if (stream.Length < offset + length) stream.SetLength(offset + length);
        return 0;
    }

    private static long Control(int descriptor, long request, nint argument)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        switch (request)
        {
            case TIOCGWINSZ:
                if (!file.Terminal) return -ENOTTY;
                ushort* size = (ushort*)argument;
                size[0] = (ushort)Math.Max(1, ConsoleSize(() => Console.WindowHeight, 24));
                size[1] = (ushort)Math.Max(1, ConsoleSize(() => Console.WindowWidth, 80));
                size[2] = size[3] = 0;
                return 0;
            case TCGETS:
                if (!file.Terminal) return -ENOTTY;
                // struct termios for a cooked terminal: ICRNL | IXON, OPOST | ONLCR, B38400 | CS8 | CREAD, ISIG | ICANON | ECHO...
                new Span<byte>((void*)argument, 60).Clear();
                uint* modes = (uint*)argument;
                modes[0] = 0x500; modes[1] = 0x5; modes[2] = 0xbf; modes[3] = 0x8a3b;
                byte* characters = (byte*)argument + 17;
                characters[0] = 3; characters[1] = 28; characters[2] = 127; characters[3] = 21; characters[4] = 4; characters[6] = 1;
                return 0;
            case 0x5402 or 0x5403 or 0x5404:                           // TCSETS, TCSETSW, TCSETSF
                return file.Terminal ? 0 : -ENOTTY;
            case FIONREAD:
                *(int*)argument = file.Pipe is Pipe pipe ? pipe.Available
                    : file.Stream is { CanSeek: true } stream ? (int)Math.Clamp(stream.Length - stream.Position, 0, int.MaxValue) : 0;
                return 0;
            case FIONBIO:
                file.Flags = *(int*)argument != 0 ? file.Flags | O_NONBLOCK : file.Flags & ~O_NONBLOCK;
                return 0;
            default:
                return -ENOTTY;
        }

        static int ConsoleSize(Func<int> read, int fallback)
        {
            try { return read(); }
            catch (Exception error) when (error is IOException or PlatformNotSupportedException) { return fallback; }
        }
    }

    private static long Open(int directory, nint pathPointer, int flags, int mode)
    {
        string path = Resolve(directory, pathPointer);
        bool closeOnExec = (flags & O_CLOEXEC) != 0;
        if (OpenDevice(path, flags) is OpenFile device) return Install(device, closeOnExec);
        if ((flags & O_TMPFILE) == O_TMPFILE) return -EOPNOTSUPP;
        bool create = (flags & O_CREAT) != 0, exclusive = (flags & O_EXCL) != 0, truncate = (flags & O_TRUNC) != 0;
        int access = flags & O_ACCMODE;
        FileSystemInfo? link = SymbolicLinkAt(path);
        if (link is not null)
        {
            if (create && exclusive) return -EEXIST;
            if ((flags & O_NOFOLLOW) != 0 && (flags & O_PATH) == 0) return -ELOOP;
        }
        if (Directory.Exists(path))
        {
            if (create && exclusive) return -EEXIST;
            if (access != O_RDONLY || create || truncate) return -EISDIR;
            // A directory the process may not read fails at open, as opendir does natively, not at the first read.
            if ((flags & O_PATH) == 0)
                using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) entries.MoveNext();
            return Install(new OpenFile(FileKind.Directory, null, path, flags), closeOnExec);
        }
        bool exists = File.Exists(path);
        if ((flags & O_DIRECTORY) != 0) return exists ? -ENOTDIR : Missing(path);
        if (exists && create && exclusive) return -EEXIST;
        if (!exists && (!create || link is not null && exclusive)) return Missing(path);
        if (!exists && !Directory.Exists(Path.GetDirectoryName(path))) return Missing(path);
        if ((flags & O_PATH) != 0) return Install(new OpenFile(FileKind.Regular, null, path, flags), closeOnExec);
        var options = new FileStreamOptions
        {
            Mode = create ? exclusive ? FileMode.CreateNew : truncate ? FileMode.Create : FileMode.OpenOrCreate : truncate ? FileMode.Truncate : FileMode.Open,
            Access = access switch { O_RDONLY => FileAccess.Read, O_WRONLY => FileAccess.Write, _ => FileAccess.ReadWrite },
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0
        };
        if (options.Access == FileAccess.Read && options.Mode is FileMode.Create or FileMode.Truncate) options.Access = FileAccess.ReadWrite;
        if (create && !OperatingSystem.IsWindows()) options.UnixCreateMode = (UnixFileMode)(mode & 0x1ff & ~fileCreationMask);
        return Install(new OpenFile(FileKind.Regular, new FileStream(path, options), path, flags & ~O_CREAT & ~O_EXCL & ~O_TRUNC), closeOnExec);
    }

    /// <summary>
    /// Paths every host provides itself: random sources, null, zero, the standard streams, the synthetic identity files and,
    /// where the host has no zoneinfo, generated TZif images.
    /// </summary>
    private static OpenFile? OpenDevice(string path, int flags)
    {
        Stream? stream = path switch
        {
            "/dev/urandom" or "/dev/random" => new RandomStream(),
            "/dev/null" => Stream.Null,
            "/dev/zero" => new ZeroStream(),
            "/etc/passwd" => new MemoryStream(Encoding.UTF8.GetBytes(PasswordFile()), writable: false),
            "/etc/group" => new MemoryStream(Encoding.UTF8.GetBytes(GroupFile()), writable: false),
            _ when HostZoneInfo.Image(path) is byte[] zone => new MemoryStream(zone, writable: false),
            _ => null
        };
        if (stream is not null) return new OpenFile(stream is MemoryStream ? FileKind.Regular : FileKind.Device, stream, null, flags);
        int console = path switch
        {
            "/dev/stdin" => 0,
            "/dev/stdout" => 1,
            "/dev/stderr" => 2,
            "/dev/tty" => (flags & O_ACCMODE) == O_RDONLY ? 0 : 1,
            _ => -1
        };
        return console < 0 ? null : StandardStream(console);
    }

    private sealed class RandomStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override int Read(byte[] buffer, int offset, int count) { HostRandom.Fill(buffer.AsSpan(offset, count)); return count; }
        public override int Read(Span<byte> buffer) { HostRandom.Fill(buffer); return buffer.Length; }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ZeroStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override int Read(byte[] buffer, int offset, int count) { buffer.AsSpan(offset, count).Clear(); return count; }
        public override int Read(Span<byte> buffer) { buffer.Clear(); return buffer.Length; }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// An in-process pipe with Linux semantics: 64 KiB of buffer, blocking or O_NONBLOCK ends, end of file once every
    /// write end is closed, EPIPE (and SIGPIPE) for writes without a reader, and atomic writes up to PIPE_BUF bytes.
    /// </summary>
    private sealed class Pipe
    {
        internal const int Capacity = 65536;
        private const int AtomicWrite = 4096;
        private readonly byte[] buffer = new byte[Capacity];
        private int start, count, readers = 1, writers = 1;

        internal int Available { get { lock (this) return count; } }

        internal short Readiness(bool writer)
        {
            lock (this)
            {
                if (writer) return (short)((readers == 0 ? POLLERR : 0) | (count < Capacity ? POLLOUT | POLLWRNORM : 0));
                return (short)((count > 0 ? POLLIN | POLLRDNORM : 0) | (writers == 0 ? POLLHUP : 0));
            }
        }

        internal void Retain(bool writer)
        {
            lock (this) { if (writer) writers++; else readers++; }
        }

        internal void Release(bool writer)
        {
            lock (this)
            {
                if (writer) writers--; else readers--;
                Monitor.PulseAll(this);
            }
            SignalIo();
        }

        internal long Read(Span<byte> target, bool nonBlocking)
        {
            if (target.IsEmpty) return 0;
            int done;
            lock (this)
            {
                while (count == 0)
                {
                    if (writers == 0) return 0;
                    if (nonBlocking) return -EAGAIN;
                    Monitor.Wait(this);
                }
                done = Math.Min(target.Length, count);
                int first = Math.Min(done, Capacity - start);
                buffer.AsSpan(start, first).CopyTo(target);
                buffer.AsSpan(0, done - first).CopyTo(target[first..]);
                start = (start + done) % Capacity;
                count -= done;
                Monitor.PulseAll(this);
            }
            SignalIo();
            return done;
        }

        internal long Write(ReadOnlySpan<byte> source, bool nonBlocking)
        {
            int written = 0;
            lock (this)
            {
                while (written < source.Length)
                {
                    if (readers == 0) return written > 0 ? written : -EPIPE;
                    int space = Capacity - count, wanted = source.Length - written;
                    if (space == 0 || source.Length <= AtomicWrite && space < wanted)
                    {
                        if (nonBlocking) return written > 0 ? written : -EAGAIN;
                        Monitor.Wait(this);
                        continue;
                    }
                    int chunk = Math.Min(space, wanted), end = (start + count) % Capacity, first = Math.Min(chunk, Capacity - end);
                    source.Slice(written, first).CopyTo(buffer.AsSpan(end));
                    source.Slice(written + first, chunk - first).CopyTo(buffer);
                    count += chunk;
                    written += chunk;
                    Monitor.PulseAll(this);
                }
            }
            SignalIo();
            return written;
        }
    }

    private static long CreatePipe(int* ends, int flags)
    {
        if ((flags & ~(O_CLOEXEC | O_NONBLOCK)) != 0) return -EINVAL;
        var pipe = new Pipe();
        int status = flags & O_NONBLOCK;
        bool closeOnExec = (flags & O_CLOEXEC) != 0;
        int reader = Install(new OpenFile(FileKind.Pipe, null, null, O_RDONLY | status) { Pipe = pipe }, closeOnExec);
        if (reader < 0) return reader;
        int writer = Install(new OpenFile(FileKind.Pipe, null, null, O_WRONLY | status) { Pipe = pipe, PipeWriter = true }, closeOnExec);
        if (writer < 0) { Close(reader); return writer; }
        (ends[0], ends[1]) = (reader, writer);
        return 0;
    }

    /// <summary>Pulsed whenever a pipe changes state, so poll and select can wait for readiness.</summary>
    private static readonly object ioChanged = new();

    private static void SignalIo()
    {
        lock (ioChanged) Monitor.PulseAll(ioChanged);
    }

    /// <summary>Readiness of one descriptor: pipes report their state; files, devices and the console are always ready.</summary>
    private static short Readiness(int descriptor, short events)
    {
        if (descriptor < 0) return 0;
        OpenFile? file = Lookup(descriptor);
        if (file is null) return POLLNVAL;
        short ready = file.Pipe is Pipe pipe ? pipe.Readiness(file.PipeWriter)
            : (short)((file.CanRead ? POLLIN | POLLRDNORM : 0) | (file.CanWrite ? POLLOUT | POLLWRNORM : 0));
        return (short)(ready & (events | POLLERR | POLLHUP | POLLNVAL));
    }

    private static long Poll(PollDescriptor* entries, int count, int timeout)
    {
        if (count < 0 || count > 1 << 20 || timeout == InvalidTimeout) return -EINVAL;
        long deadline = timeout < 0 ? long.MaxValue : Stopwatch.GetTimestamp() + timeout * Stopwatch.Frequency / 1000;
        lock (ioChanged)
            while (true)
            {
                int ready = 0;
                for (int index = 0; index < count; index++)
                    if ((entries[index].ReturnedEvents = Readiness(entries[index].Descriptor, entries[index].Events)) != 0) ready++;
                if (ready > 0 || timeout == 0) return ready;
                if (!WaitForIo(deadline)) return 0;
            }
    }

    private static long Select(int count, ulong* read, ulong* write, ulong* exceptional, int timeout)
    {
        if (count is < 0 or > 1024 || timeout == InvalidTimeout) return -EINVAL;
        int words = (count + 63) / 64;
        Span<ulong> wantRead = stackalloc ulong[words], wantWrite = stackalloc ulong[words];
        for (int word = 0; word < words; word++)
        {
            wantRead[word] = read == null ? 0 : read[word];
            wantWrite[word] = write == null ? 0 : write[word];
        }
        long deadline = timeout < 0 ? long.MaxValue : Stopwatch.GetTimestamp() + timeout * Stopwatch.Frequency / 1000;
        lock (ioChanged)
            while (true)
            {
                int ready = 0;
                for (int descriptor = 0; descriptor < count; descriptor++)
                {
                    ulong bit = 1UL << (descriptor % 64);
                    bool wantsRead = (wantRead[descriptor / 64] & bit) != 0, wantsWrite = (wantWrite[descriptor / 64] & bit) != 0;
                    bool wantsException = exceptional != null && (exceptional[descriptor / 64] & bit) != 0;
                    if (!wantsRead && !wantsWrite && !wantsException) continue;
                    short state = Readiness(descriptor, POLLIN | POLLOUT);
                    if (state == POLLNVAL) return -EBADF;
                    bool canRead = wantsRead && (state & (POLLIN | POLLHUP | POLLERR)) != 0, canWrite = wantsWrite && (state & (POLLOUT | POLLERR)) != 0;
                    if (read != null) read[descriptor / 64] = canRead ? read[descriptor / 64] | bit : read[descriptor / 64] & ~bit;
                    if (write != null) write[descriptor / 64] = canWrite ? write[descriptor / 64] | bit : write[descriptor / 64] & ~bit;
                    if (exceptional != null) exceptional[descriptor / 64] &= ~bit;
                    ready += (canRead ? 1 : 0) + (canWrite ? 1 : 0);
                }
                if (ready > 0 || timeout == 0) return ready;
                if (!WaitForIo(deadline)) return 0;
                for (int word = 0; word < words; word++)
                {
                    if (read != null) read[word] = wantRead[word];
                    if (write != null) write[word] = wantWrite[word];
                }
            }
    }

    /// <summary>Waits on <see cref="ioChanged"/> (which the caller holds) until a change or the deadline; false at the deadline.</summary>
    private static bool WaitForIo(long deadline)
    {
        if (deadline == long.MaxValue) { Monitor.Wait(ioChanged); return true; }
        long remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return false;
        Monitor.Wait(ioChanged, TimeSpan.FromTicks(Math.Max(1, remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
        return true;
    }

    /// <summary>Marks an invalid timeout argument of select, pselect6 or ppoll (EINVAL); any other negative value waits forever.</summary>
    private const int InvalidTimeout = int.MinValue;

    private static int MillisecondsFromTimeval(long* time) =>
        time[0] < 0 || time[1] is < 0 or >= 1_000_000 ? InvalidTimeout : (int)Math.Min(int.MaxValue, time[0] * 1000 + (time[1] + 999) / 1000);

    private static int MillisecondsFromTimespec(Timespec* time) =>
        time->Duration is TimeSpan duration ? (int)Math.Min(int.MaxValue, Math.Ceiling(duration.TotalMilliseconds)) : InvalidTimeout;
}
