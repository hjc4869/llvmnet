using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LlvmNet.Runtime;

/// <summary>
/// The host system-call layer for musl on dotnet64-v2 (plan items LC-01, LC-04..LC-10). musl's
/// arch/llvmnet/syscall_arch.h sends every system call here. Numbers, argument layouts, structure
/// layouts and error results follow Linux x86-64, so musl runs unmodified; the implementation uses
/// only .NET APIs, so it behaves the same on every .NET host. Unimplemented calls return -ENOSYS.
/// </summary>
public static unsafe class HostSyscalls
{
    private const long ENOENT = 2, EINTR = 4, EIO = 5, EBADF = 9, EAGAIN = 11, ENOMEM = 12, EACCES = 13, EFAULT = 14,
        EEXIST = 17, ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ENOTTY = 25, ESPIPE = 29, ERANGE = 34, ENAMETOOLONG = 36,
        ENOSYS = 38, ENOTEMPTY = 39, ETIMEDOUT = 110;
    private const int AtFdCwd = -100;

    private sealed class OpenFile(Stream? stream, string? path, int flags, bool directory)
    {
        internal readonly Stream? Stream = stream;
        internal readonly string? Path = path;
        internal int Flags = flags;
        internal readonly bool Directory = directory;
        internal List<string>? Entries;
        internal int EntryIndex;
        internal int References = 1;
    }

    private sealed record Mapping(nint Base, long Length);

    private static readonly object gate = new();
    private static readonly Dictionary<int, OpenFile> files = new()
    {
        [0] = new OpenFile(Console.OpenStandardInput(), null, 0, false),
        [1] = new OpenFile(Console.OpenStandardOutput(), null, 1, false),
        [2] = new OpenFile(Console.OpenStandardError(), null, 1, false),
    };
    private static readonly Dictionary<nint, Mapping> mappings = [];
    private static readonly ConcurrentDictionary<nint, object> futexes = new();
    private static readonly ConcurrentDictionary<int, nint> signalHandlers = new();
    [ThreadStatic] private static nint threadPointer;
    [ThreadStatic] private static int threadId;
    private static int nextThreadId = 1;

    [CExport("__llvmnet_get_tp")]
    public static long GetThreadPointer() => threadPointer;

    internal static int CurrentThreadId => threadId != 0 ? threadId : threadId = Interlocked.Increment(ref nextThreadId) + 100;

    [CExport("__llvmnet_syscall")]
    public static long Syscall(long number, long a, long b, long c, long d, long e, long f)
    {
        try
        {
            return number switch
            {
                0 => Read((int)a, (nint)b, c),
                1 => Write((int)a, (nint)b, c),
                2 => Open(AtFdCwd, (nint)a, (int)b, (int)c),
                3 => Close((int)a),
                4 => Stat(AtFdCwd, (nint)a, (nint)b, 0),
                5 => Stat((int)a, 0, (nint)b, 0x1000),
                6 => Stat(AtFdCwd, (nint)a, (nint)b, 0x100),
                8 => Seek((int)a, b, (int)c),
                9 => Map((nint)a, b, (int)c, (int)d, (int)e, f),
                10 or 28 => 0,                                  // mprotect, madvise
                11 => Unmap((nint)a, b),
                12 => 0,                                        // brk: report no heap so mallocng uses mmap
                13 => SignalAction((int)a, (nint)b, (nint)c),
                14 => SignalMask((nint)c, d),
                16 => Control((int)a, b, (nint)c),
                19 => Vector((int)a, (nint)b, (int)c, read: true),
                20 => Vector((int)a, (nint)b, (int)c, read: false),
                21 => Access(AtFdCwd, (nint)a, (int)b),
                24 => Yield(),
                25 => Remap((nint)a, b, c, (int)d),
                32 => Duplicate((int)a, -1),
                33 => Duplicate((int)a, (int)b),
                35 => Sleep((nint)a),
                39 => Environment.ProcessId,
                60 => ExitThread((int)a),
                62 => Kill((int)a == Environment.ProcessId ? CurrentThreadId : -1, (int)b),
                63 => Name((nint)a),
                72 => FileControl((int)a, (int)b, c),
                74 => Synchronize((int)a),
                77 => Truncate((int)a, b),
                79 => CurrentDirectory((nint)a, b),
                80 => ChangeDirectory((nint)a),
                82 => Rename(AtFdCwd, (nint)a, AtFdCwd, (nint)b),
                83 => MakeDirectory(AtFdCwd, (nint)a),
                84 => Unlink(AtFdCwd, (nint)a, 0x200),
                87 => Unlink(AtFdCwd, (nint)a, 0),
                89 => -EINVAL,                                  // readlink: no symbolic links are reported
                95 => 18,                                       // umask: report 022
                96 => TimeOfDay((nint)a),
                102 or 104 or 107 or 108 => 1000,               // getuid, getgid, geteuid, getegid
                110 => 1,                                       // getppid
                158 => b == 0x1002 ? SetThreadPointer((nint)b) : -EINVAL,
                186 => CurrentThreadId,
                200 => Kill((int)a, (int)b),
                201 => Time((nint)a),
                202 => Futex((nint)a, (int)b, (int)c, (nint)d),
                204 => Affinity((nint)c, b),
                205 => SetThreadPointer((nint)a),
                217 => DirectoryEntries((int)a, (nint)b, (int)c),
                218 => CurrentThreadId,                         // set_tid_address
                228 => ClockTime((int)a, (nint)b),
                229 => ClockResolution((nint)b),
                230 => ClockSleep((int)a, (int)b, (nint)c),
                231 => ExitProcess((int)a),
                234 => Kill((int)b, (int)c),
                257 => Open((int)a, (nint)b, (int)c, (int)d),
                258 => MakeDirectory((int)a, (nint)b),
                262 => Stat((int)a, (nint)b, (nint)c, (int)d),
                263 => Unlink((int)a, (nint)b, (int)c),
                264 or 316 => Rename((int)a, (nint)b, (int)c, (nint)d),
                267 => -EINVAL,                                 // readlinkat
                280 => SetTimes((int)a, (nint)b, (nint)c, (int)d),
                269 or 439 => Access((int)a, (nint)b, (int)c),
                292 => Duplicate((int)a, (int)b),
                302 => ResourceLimit((int)b, (nint)d),
                318 => Random((nint)a, b),
                _ => -ENOSYS
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return -Errno(error);
        }
    }

    private static long Errno(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => ENOENT,
        UnauthorizedAccessException => EACCES,
        PathTooLongException => ENAMETOOLONG,
        ArgumentException => EINVAL,
        NotSupportedException => EINVAL,
        IOException io when (io.HResult & 0xffff) is > 0 and < 4096 && OperatingSystem.IsLinux() => io.HResult & 0xffff,
        _ => EIO
    };

    private static string Text(nint pointer) => pointer == 0 ? throw new ArgumentException("null path") : Marshal.PtrToStringUTF8(pointer)!;

    private static string Resolve(int directory, nint pathPointer)
    {
        string path = Text(pathPointer);
        if (path.Length == 0) throw new FileNotFoundException(path);
        if (Path.IsPathRooted(path)) return path;
        if (directory == AtFdCwd) return Path.GetFullPath(path);
        OpenFile parent = Lookup(directory) ?? throw new ArgumentException("bad directory descriptor");
        return Path.GetFullPath(Path.Combine(parent.Path ?? throw new ArgumentException("not a directory"), path));
    }

    private static OpenFile? Lookup(int descriptor)
    {
        lock (gate) return files.GetValueOrDefault(descriptor);
    }

    private static int Install(OpenFile file, int minimum = 0)
    {
        lock (gate)
        {
            int descriptor = minimum;
            while (files.ContainsKey(descriptor)) descriptor++;
            files[descriptor] = file;
            return descriptor;
        }
    }

    private static long Read(int descriptor, nint buffer, long count)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Directory) return -EISDIR;
        if (file.Stream is null || !file.Stream.CanRead) return -EBADF;
        return file.Stream.Read(new Span<byte>((void*)buffer, checked((int)Math.Min(count, int.MaxValue))));
    }

    private static long Write(int descriptor, nint buffer, long count)
    {
        OpenFile? file = Lookup(descriptor);
        if (file?.Stream is null || !file.Stream.CanWrite) return -EBADF;
        if ((file.Flags & 1024) != 0 && file.Stream.CanSeek) file.Stream.Seek(0, SeekOrigin.End);
        int length = checked((int)Math.Min(count, int.MaxValue));
        file.Stream.Write(new ReadOnlySpan<byte>((void*)buffer, length));
        if (descriptor is 1 or 2) file.Stream.Flush();
        return length;
    }

    private static long Vector(int descriptor, nint vectors, int count, bool read)
    {
        long total = 0;
        for (int index = 0; index < count; index++)
        {
            nint start = ((nint*)vectors)[index * 2];
            long length = ((long*)vectors)[index * 2 + 1];
            if (length == 0) continue;
            long done = read ? Read(descriptor, start, length) : Write(descriptor, start, length);
            if (done < 0) return total > 0 ? total : done;
            total += done;
            if (done < length) break;
        }
        return total;
    }

    /// <summary>A byte source for /dev/urandom and /dev/random backed by the operating system's random generator.</summary>
    private sealed class RandomStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override int Read(byte[] buffer, int offset, int count) { RandomNumberGenerator.Fill(buffer.AsSpan(offset, count)); return count; }
        public override int Read(Span<byte> buffer) { RandomNumberGenerator.Fill(buffer); return buffer.Length; }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Device paths every host provides, independent of the host's own /dev: random sources, null, zero and the standard streams.</summary>
    private static Stream? Device(string path) => path switch
    {
        "/dev/urandom" or "/dev/random" => new RandomStream(),
        "/dev/null" => Stream.Null,
        "/dev/zero" => new ZeroStream(),
        "/dev/stdin" => Console.OpenStandardInput(),
        "/dev/stdout" or "/dev/tty" => Console.OpenStandardOutput(),
        "/dev/stderr" => Console.OpenStandardError(),
        _ => null
    };

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

    private static long Open(int directory, nint pathPointer, int flags, int mode)
    {
        string path = Resolve(directory, pathPointer);
        if (Device(path) is Stream device) return Install(new OpenFile(device, null, flags, false));
        bool create = (flags & 64) != 0, exclusive = (flags & 128) != 0, truncate = (flags & 512) != 0;
        if ((flags & 65536) != 0 || Directory.Exists(path) && !create)
        {
            if (!Directory.Exists(path)) return File.Exists(path) ? -ENOTDIR : -ENOENT;
            if ((flags & 3) != 0) return -EISDIR;
            return Install(new OpenFile(null, path, flags, true));
        }
        if (exclusive && create && (File.Exists(path) || Directory.Exists(path))) return -EEXIST;
        if (!create && !File.Exists(path)) return -ENOENT;
        FileAccess access = (flags & 3) switch { 0 => FileAccess.Read, 1 => FileAccess.Write, _ => FileAccess.ReadWrite };
        FileMode fileMode = create ? exclusive ? FileMode.CreateNew : truncate ? FileMode.Create : FileMode.OpenOrCreate : truncate ? FileMode.Truncate : FileMode.Open;
        if (access == FileAccess.Read && fileMode is FileMode.Create or FileMode.Truncate) access = FileAccess.ReadWrite;
        var options = new FileStreamOptions { Mode = fileMode, Access = access, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0 };
        if (create && !OperatingSystem.IsWindows()) options.UnixCreateMode = (UnixFileMode)(mode & 0x1ff & ~18);
        return Install(new OpenFile(new FileStream(path, options), path, flags, false));
    }

    private static long Close(int descriptor)
    {
        OpenFile? file;
        lock (gate)
        {
            if (!files.Remove(descriptor, out file)) return -EBADF;
            if (--file.References > 0) return 0;
        }
        if (descriptor > 2) file.Stream?.Dispose();
        return 0;
    }

    private static long Duplicate(int descriptor, int target)
    {
        lock (gate)
        {
            if (!files.TryGetValue(descriptor, out OpenFile? file)) return -EBADF;
            if (target == descriptor) return target;
            if (target >= 0 && files.Remove(target, out OpenFile? replaced) && --replaced.References == 0 && target > 2) replaced.Stream?.Dispose();
            file.References++;
            if (target < 0) { target = 0; while (files.ContainsKey(target)) target++; }
            files[target] = file;
            return target;
        }
    }

    private static long FileControl(int descriptor, int command, long argument)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        return command switch
        {
            0 or 1030 => Install(Retain(file), (int)argument),   // F_DUPFD, F_DUPFD_CLOEXEC
            1 or 2 => 0,                                          // F_GETFD, F_SETFD
            3 => file.Flags,                                      // F_GETFL
            4 => SetFlags(file, (int)argument),                   // F_SETFL
            5 or 6 or 7 => 0,                                     // advisory locks always succeed
            _ => -EINVAL
        };

        static OpenFile Retain(OpenFile file) { lock (gate) file.References++; return file; }
        static long SetFlags(OpenFile file, int flags) { file.Flags = file.Flags & 3 | flags & ~3; return 0; }
    }

    private static long Seek(int descriptor, long offset, int whence)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Directory) { if (offset == 0 && whence == 0) { file.EntryIndex = 0; file.Entries = null; } return 0; }
        if (file.Stream is null || !file.Stream.CanSeek) return -ESPIPE;
        long position = whence switch { 0 => offset, 1 => file.Stream.Position + offset, 2 => file.Stream.Length + offset, _ => -1 };
        if (position < 0) return -EINVAL;
        return file.Stream.Seek(position, SeekOrigin.Begin);
    }

    private static long Synchronize(int descriptor)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Stream is FileStream stream) stream.Flush(true);
        return 0;
    }

    private static long Truncate(int descriptor, long length)
    {
        OpenFile? file = Lookup(descriptor);
        if (file?.Stream is not FileStream stream) return file is null ? -EBADF : -EINVAL;
        if (length < 0) return -EINVAL;
        long position = stream.Position;
        stream.SetLength(length);
        stream.Position = position;
        return 0;
    }

    private static long Control(int descriptor, long request, nint argument)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        bool terminal = descriptor switch { 0 => !Console.IsInputRedirected, 1 => !Console.IsOutputRedirected, 2 => !Console.IsErrorRedirected, _ => false };
        if (request != 0x5413 || !terminal) return -ENOTTY;   // TIOCGWINSZ
        ushort* size = (ushort*)argument;
        size[0] = (ushort)Math.Max(1, SafeConsole(() => Console.WindowHeight));
        size[1] = (ushort)Math.Max(1, SafeConsole(() => Console.WindowWidth));
        size[2] = size[3] = 0;
        return 0;

        static int SafeConsole(Func<int> read) { try { return read(); } catch (IOException) { return 80; } }
    }

    private static long Stat(int directory, nint pathPointer, nint buffer, int flags)
    {
        string? path;
        bool console = false;
        if ((flags & 0x1000) != 0 && (pathPointer == 0 || *(byte*)pathPointer == 0))
        {
            OpenFile? file = Lookup(directory);
            if (file is null) return -EBADF;
            path = file.Path;
            console = path is null;
        }
        else path = Resolve(directory, pathPointer);
        long* stat = (long*)buffer;
        new Span<byte>((void*)buffer, 144).Clear();
        if (console)
        {
            ((uint*)buffer)[6] = 0x2190;                          // S_IFCHR | 0620
            stat[7] = 1024;
            return 0;
        }
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path!) : new FileInfo(path!);
        if (!info.Exists) return -ENOENT;
        bool isDirectory = info is DirectoryInfo;
        uint permissions = OperatingSystem.IsWindows() ? (isDirectory ? 0x1edu : 0x1a4u) : (uint)File.GetUnixFileMode(path!);
        stat[0] = 1;                                                // st_dev
        stat[1] = (long)(uint)StringComparer.Ordinal.GetHashCode(Path.GetFullPath(path!)) + 2;  // st_ino
        stat[2] = 1;                                                // st_nlink
        ((uint*)buffer)[6] = (isDirectory ? 0x4000u : 0x8000u) | permissions;   // st_mode
        ((uint*)buffer)[7] = 1000;                                  // st_uid
        ((uint*)buffer)[8] = 1000;                                  // st_gid
        stat[6] = isDirectory ? 4096 : ((FileInfo)info).Length;     // st_size
        stat[7] = 4096;                                             // st_blksize
        stat[8] = (stat[6] + 511) / 512;                            // st_blocks
        WriteTime(stat + 9, info.LastAccessTimeUtc);
        WriteTime(stat + 11, info.LastWriteTimeUtc);
        WriteTime(stat + 13, info.LastWriteTimeUtc);
        return 0;

        static void WriteTime(long* target, DateTime time)
        {
            long ticks = (time - DateTime.UnixEpoch).Ticks;
            target[0] = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long remainder);
            target[1] = remainder * 100;
        }
    }

    /// <summary>utimensat: a null path names the descriptor itself (futimens); UTIME_NOW and UTIME_OMIT as in Linux.</summary>
    private static long SetTimes(int directory, nint pathPointer, nint times, int flags)
    {
        const long UtimeNow = (1L << 30) - 1, UtimeOmit = (1L << 30) - 2;
        string? path = pathPointer == 0 ? Lookup(directory)?.Path : Resolve(directory, pathPointer);
        if (path is null) return pathPointer == 0 && Lookup(directory) is null ? -EBADF : -EINVAL;
        if (!File.Exists(path) && !Directory.Exists(path)) return -ENOENT;
        DateTime now = DateTime.UtcNow;
        for (int index = 0; index < 2; index++)
        {
            long seconds = times == 0 ? 0 : ((long*)times)[index * 2], nanoseconds = times == 0 ? UtimeNow : ((long*)times)[index * 2 + 1];
            if (nanoseconds == UtimeOmit) continue;
            if (nanoseconds != UtimeNow && nanoseconds is < 0 or >= 1000000000) return -EINVAL;
            DateTime time = nanoseconds == UtimeNow ? now : DateTime.UnixEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + nanoseconds / 100);
            if (index == 0) File.SetLastAccessTimeUtc(path, time);
            else File.SetLastWriteTimeUtc(path, time);
        }
        return 0;
    }

    private static long Access(int directory, nint pathPointer, int mode)
    {
        string path = Resolve(directory, pathPointer);
        return File.Exists(path) || Directory.Exists(path) ? 0 : -ENOENT;
    }

    private static long DirectoryEntries(int descriptor, nint buffer, int count)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (!file.Directory) return -ENOTDIR;
        file.Entries ??= [".", "..", .. Directory.EnumerateFileSystemEntries(file.Path!).Select(Path.GetFileName).OfType<string>()];
        int written = 0;
        while (file.EntryIndex < file.Entries.Count)
        {
            string name = file.Entries[file.EntryIndex];
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            int length = (19 + bytes.Length + 1 + 7) & ~7;
            if (written + length > count) break;
            byte* record = (byte*)buffer + written;
            new Span<byte>(record, length).Clear();
            string full = Path.Combine(file.Path!, name);
            *(ulong*)record = (ulong)(uint)StringComparer.Ordinal.GetHashCode(Path.GetFullPath(full)) + 2;
            *(long*)(record + 8) = file.EntryIndex + 1;
            *(ushort*)(record + 16) = (ushort)length;
            record[18] = (byte)(Directory.Exists(full) ? 4 : 8);
            bytes.CopyTo(new Span<byte>(record + 19, bytes.Length));
            written += length;
            file.EntryIndex++;
        }
        return written == 0 && file.EntryIndex < file.Entries.Count ? -EINVAL : written;
    }

    private static long MakeDirectory(int directory, nint pathPointer)
    {
        string path = Resolve(directory, pathPointer);
        if (File.Exists(path) || Directory.Exists(path)) return -EEXIST;
        if (!Directory.Exists(Path.GetDirectoryName(path))) return -ENOENT;
        Directory.CreateDirectory(path);
        return 0;
    }

    private static long Unlink(int directory, nint pathPointer, int flags)
    {
        string path = Resolve(directory, pathPointer);
        if ((flags & 0x200) != 0)
        {
            if (!Directory.Exists(path)) return File.Exists(path) ? -ENOTDIR : -ENOENT;
            if (Directory.EnumerateFileSystemEntries(path).Any()) return -ENOTEMPTY;
            Directory.Delete(path);
            return 0;
        }
        if (Directory.Exists(path)) return -EISDIR;
        if (!File.Exists(path)) return -ENOENT;
        File.Delete(path);
        return 0;
    }

    private static long Rename(int fromDirectory, nint from, int toDirectory, nint to)
    {
        string source = Resolve(fromDirectory, from), destination = Resolve(toDirectory, to);
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        else return -ENOENT;
        return 0;
    }

    private static long CurrentDirectory(nint buffer, long size)
    {
        byte[] path = Encoding.UTF8.GetBytes(Directory.GetCurrentDirectory().Replace('\\', '/'));
        if (path.Length + 1 > size) return -ERANGE;
        path.CopyTo(new Span<byte>((void*)buffer, path.Length));
        ((byte*)buffer)[path.Length] = 0;
        return path.Length + 1;
    }

    private static long ChangeDirectory(nint pathPointer)
    {
        string path = Resolve(AtFdCwd, pathPointer);
        if (!Directory.Exists(path)) return File.Exists(path) ? -ENOTDIR : -ENOENT;
        Directory.SetCurrentDirectory(path);
        return 0;
    }

    private static long Map(nint address, long length, int protection, int flags, int descriptor, long offset)
    {
        if (length <= 0 || (flags & 0x10) != 0) return -EINVAL;   // MAP_FIXED is not supported
        long size = (length + 4095) & ~4095L;
        nint memory = (nint)NativeMemory.AlignedAlloc((nuint)size, 4096);
        if (memory == 0) return -ENOMEM;
        NativeMemory.Clear((void*)memory, (nuint)size);
        if ((flags & 0x20) == 0)
        {
            OpenFile? file = Lookup(descriptor);
            if (file?.Stream is not FileStream stream) { NativeMemory.AlignedFree((void*)memory); return -EBADF; }
            RandomAccess.Read(stream.SafeFileHandle, new Span<byte>((void*)memory, checked((int)Math.Min(length, int.MaxValue))), offset);
        }
        lock (gate) mappings[memory] = new Mapping(memory, size);
        return memory;
    }

    private static long Unmap(nint address, long length)
    {
        lock (gate)
        {
            if (mappings.Remove(address, out Mapping? mapping) && (length + 4095 & ~4095L) >= mapping.Length)
            {
                NativeMemory.AlignedFree((void*)address);
                return 0;
            }
            if (mapping is not null) mappings[address] = mapping;   // partial unmaps keep the memory until the whole region goes
        }
        return 0;
    }

    private static long Remap(nint address, long oldLength, long newLength, int flags)
    {
        if ((flags & 1) == 0) return -ENOMEM;                         // growing in place is not possible
        long result = Map(0, newLength, 3, 0x22, -1, 0);
        if (result < 0) return result;
        Buffer.MemoryCopy((void*)address, (void*)result, newLength, Math.Min(oldLength, newLength));
        Unmap(address, oldLength);
        return result;
    }

    private static long SetThreadPointer(nint pointer)
    {
        threadPointer = pointer;
        return 0;
    }

    private static long Futex(nint address, int operation, int value, nint timeout)
    {
        object queue = futexes.GetOrAdd(address, _ => new object());
        switch (operation & 127)
        {
            case 0 or 9:                                              // FUTEX_WAIT, FUTEX_WAIT_BITSET
                lock (queue)
                {
                    if (Volatile.Read(ref *(int*)address) != value) return -EAGAIN;
                    if (timeout == 0) { Monitor.Wait(queue); return 0; }
                    long* time = (long*)timeout;
                    TimeSpan wait = TimeSpan.FromTicks(time[0] * TimeSpan.TicksPerSecond + time[1] / 100);
                    if ((operation & 127) == 9)
                        wait -= TimeSpan.FromTicks((operation & 256) != 0 ? (DateTime.UtcNow - DateTime.UnixEpoch).Ticks : Stopwatch.GetElapsedTime(0).Ticks);
                    return wait <= TimeSpan.Zero || !Monitor.Wait(queue, wait) ? -ETIMEDOUT : 0;
                }
            case 1 or 10:                                             // FUTEX_WAKE, FUTEX_WAKE_BITSET
                lock (queue)
                {
                    if (value == int.MaxValue) Monitor.PulseAll(queue);
                    else for (int index = 0; index < value; index++) Monitor.Pulse(queue);
                }
                return value;
            case 3 or 4:                                              // FUTEX_REQUEUE, FUTEX_CMP_REQUEUE: wake all; spurious wakeups are allowed
                lock (queue) Monitor.PulseAll(queue);
                return value;
            default:
                return -ENOSYS;
        }
    }

    private static long Yield()
    {
        Thread.Yield();
        return 0;
    }

    private static long Sleep(nint request)
    {
        long* time = (long*)request;
        if (time[0] < 0 || time[1] is < 0 or >= 1000000000) return -EINVAL;
        Thread.Sleep(TimeSpan.FromTicks(time[0] * TimeSpan.TicksPerSecond + time[1] / 100));
        return 0;
    }

    private static long ClockSleep(int clock, int flags, nint request)
    {
        if ((flags & 1) == 0) return Sleep(request);                  // TIMER_ABSTIME
        long* time = (long*)request;
        long* now = stackalloc long[2];
        ClockTime(clock, (nint)now);
        long nanoseconds = (time[0] - now[0]) * 1000000000 + time[1] - now[1];
        if (nanoseconds > 0) Thread.Sleep(TimeSpan.FromTicks(nanoseconds / 100));
        return 0;
    }

    private static readonly long processStart = Stopwatch.GetTimestamp();

    private static long ClockTime(int clock, nint result)
    {
        long* time = (long*)result;
        long ticks = clock switch
        {
            0 or 5 => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks,                                  // REALTIME
            1 or 4 or 6 or 7 => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)), // MONOTONIC
            2 or 3 => Process.GetCurrentProcess().TotalProcessorTime.Ticks,                           // CPU time
            _ => -1
        };
        if (ticks < 0) return -EINVAL;
        time[0] = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long remainder);
        time[1] = remainder * 100;
        return 0;
    }

    private static long ClockResolution(nint result)
    {
        if (result != 0) { ((long*)result)[0] = 0; ((long*)result)[1] = 100; }
        return 0;
    }

    private static long TimeOfDay(nint result)
    {
        if (result == 0) return 0;
        long ticks = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks;
        ((long*)result)[0] = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long remainder);
        ((long*)result)[1] = remainder / 10;
        return 0;
    }

    private static long Time(nint result)
    {
        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (result != 0) *(long*)result = seconds;
        return seconds;
    }

    private static long Random(nint buffer, long length)
    {
        int count = checked((int)Math.Min(length, 33554431));
        RandomNumberGenerator.Fill(new Span<byte>((void*)buffer, count));
        return count;
    }

    private static long Name(nint buffer)
    {
        string[] fields =
        [
            OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
            Environment.MachineName, Environment.OSVersion.Version.ToString(), "llvmnet dotnet64-v2",
            RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", var other => other.ToString().ToLowerInvariant() },
            "(none)"
        ];
        new Span<byte>((void*)buffer, 65 * 6).Clear();
        for (int index = 0; index < fields.Length; index++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(fields[index]);
            bytes.AsSpan(0, Math.Min(bytes.Length, 64)).CopyTo(new Span<byte>((byte*)buffer + 65 * index, 64));
        }
        return 0;
    }

    private static long Affinity(nint mask, long size)
    {
        int bytes = (int)Math.Min(size, 128);
        new Span<byte>((void*)mask, bytes).Clear();
        for (int processor = 0; processor < Math.Min(Environment.ProcessorCount, bytes * 8); processor++)
            ((byte*)mask)[processor / 8] |= (byte)(1 << (processor % 8));
        return bytes;
    }

    private static long ResourceLimit(int resource, nint old)
    {
        if (old == 0) return 0;
        ulong limit = resource switch { 3 => 8UL << 20, 7 => 1024, _ => ulong.MaxValue };
        ((ulong*)old)[0] = ((ulong*)old)[1] = limit;
        return 0;
    }

    private static long SignalAction(int signal, nint action, nint old)
    {
        if (signal is <= 0 or > 64 or 9 or 19) return -EINVAL;
        if (old != 0)
        {
            new Span<byte>((void*)old, 32).Clear();
            *(nint*)old = signalHandlers.GetValueOrDefault(signal);
        }
        if (action != 0) signalHandlers[signal] = *(nint*)action;
        return 0;
    }

    private static long SignalMask(nint old, long size)
    {
        if (old != 0) new Span<byte>((void*)old, (int)Math.Clamp(size, 0, 128)).Clear();
        return 0;
    }

    /// <summary>
    /// Signals are synchronous: a signal sent to this process runs its handler on the calling thread, is
    /// ignored, or terminates the process with status 128+signal, as the default action would.
    /// Asynchronous delivery from outside the process is not supported.
    /// </summary>
    private static long Kill(int thread, int signal)
    {
        if (signal == 0) return 0;
        if (signal is < 0 or > 64) return -EINVAL;
        nint handler = signalHandlers.GetValueOrDefault(signal);
        if (handler == 1) return 0;                                    // SIG_IGN
        if (handler != 0)
        {
            ((delegate* managed<int, void>)handler)(signal);
            return 0;
        }
        if (signal is 17 or 18 or 23 or 28) return 0;                  // default action ignores SIGCHLD, SIGCONT, SIGURG, SIGWINCH
        Console.Out.Flush();
        Console.Error.WriteLine(signal == 6 ? "Aborted" : $"Terminated by signal {signal}");
        Environment.Exit(128 + signal);
        return 0;
    }

    private static long ExitProcess(int status)
    {
        Environment.Exit(status);
        return 0;
    }

    private sealed class ThreadExit : Exception;
    [ThreadStatic] private static bool clonedThread;
    private static int clonedThreads;

    /// <summary>
    /// musl's __clone for pthread_create (LC-02): runs <paramref name="function"/>(<paramref name="argument"/>) on a new
    /// managed thread with CLONE_SETTLS, CLONE_PARENT_SETTID and CLONE_CHILD_CLEARTID semantics. SYS_exit on that
    /// thread unwinds to here, then the child-tid word is cleared and its futex woken, as the kernel does.
    /// </summary>
    [CExport("__llvmnet_clone")]
    public static int Clone(nint function, nint argument, int flags, nint parentTid, nint tls, nint childTid)
    {
        const int SetTls = 0x80000, ParentSetTid = 0x100000, ChildClearTid = 0x200000;
        int tid = Interlocked.Increment(ref nextThreadId) + 100;
        if ((flags & ParentSetTid) != 0) Volatile.Write(ref *(int*)parentTid, tid);
        Interlocked.Increment(ref clonedThreads);
        var thread = new Thread(() =>
        {
            threadId = tid;
            clonedThread = true;
            if ((flags & SetTls) != 0) threadPointer = tls;
            try { ((delegate* managed<nint, int>)function)(argument); }
            catch (ThreadExit) { }
            finally
            {
                if ((flags & ChildClearTid) != 0)
                {
                    Volatile.Write(ref *(int*)childTid, 0);
                    Futex(childTid, 1, 1, 0);
                }
                Interlocked.Decrement(ref clonedThreads);
            }
        }, 8 << 20)
        { IsBackground = true, Name = $"musl thread {tid}" };
        thread.Start();
        return tid;
    }

    private static long ExitThread(int status)
    {
        if (clonedThread) throw new ThreadExit();
        // pthread_exit on the main thread: the process ends once every other thread has exited.
        SpinWait.SpinUntil(() => Volatile.Read(ref clonedThreads) == 0);
        return ExitProcess(status);
    }
}
