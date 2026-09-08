using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe class Posix
{
    private sealed record Mapping(MemoryMappedFile File, MemoryMappedViewAccessor View, long Size);
    private sealed class DirectoryState(string path)
    {
        internal readonly IEnumerator<string> Names = new[] { Path.Combine(path, "."), Path.Combine(path, "..") }.Concat(Directory.EnumerateFileSystemEntries(path)).GetEnumerator();
        internal readonly nint Entry = (nint)NativeMemory.AllocZeroed(280);
        internal long Position;
    }
    private static readonly Dictionary<nint, Mapping> mappings = [];
    private static readonly Dictionary<nint, DirectoryState> directories = [];
    private static readonly Dictionary<int, nint> errorStrings = [];

    [CExport("getcwd")]
    public static nint Getcwd(nint buffer, long size)
    {
        if (size < 0 || buffer != 0 && size == 0)
        {
            ProcessRuntime.Error(22);
            return 0;
        }
        try
        {
            byte[] path = Encoding.UTF8.GetBytes(Directory.GetCurrentDirectory());
            long required = (long)path.Length + 1;
            if (buffer == 0 && size == 0) size = required;
            if (size < required)
            {
                ProcessRuntime.Error(34);
                return 0;
            }
            if (buffer == 0) buffer = CString.Malloc(size);
            if (buffer == 0) return 0;
            path.AsSpan().CopyTo(new Span<byte>((void*)buffer, path.Length));
            ((byte*)buffer)[path.Length] = 0;
            return buffer;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ProcessRuntime.Error(error is UnauthorizedAccessException ? 13 : 2);
            return 0;
        }
    }

    [CExport("chdir")]
    public static int Chdir(nint path)
    {
        if (path == 0) return ProcessRuntime.Error(14);
        string name = CString.Text(path);
        if (name.Length == 0) return ProcessRuntime.Error(2);
        if (File.Exists(name)) return ProcessRuntime.Error(20);
        try { Directory.SetCurrentDirectory(name); return 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ProcessRuntime.Error(error is UnauthorizedAccessException ? 13 : error is PathTooLongException ? 36 : error is ArgumentException ? 22 : 2);
        }
    }

    [CExport("strerror")]
    public static nint Strerror(int error)
    {
        lock (errorStrings)
        {
            if (!errorStrings.TryGetValue(error, out nint text))
            {
                text = Marshal.StringToCoTaskMemUTF8(AbiContract.Portable ? PortableErrors.Message(error) : new Win32Exception(error).Message);
                errorStrings[error] = text;
            }
            return text;
        }
    }
    [CExport("strerror_r")]
    public static nint StrerrorR(int error, nint buffer, long size) => Strerror(error);
    [CExport("__xpg_strerror_r")]
    public static int XpgStrerrorR(int error, nint buffer, long size)
    {
        nint text = Strerror(error);
        long length = CString.Strlen(text);
        if (size <= length) return 34;
        CString.Memcpy(buffer, text, length + 1);
        return 0;
    }
    [CExport("perror")]
    public static void Perror(nint prefix)
    {
        string text = prefix == 0 ? "" : CString.Text(prefix);
        Console.Error.WriteLine((text.Length == 0 ? "" : text + ": ") + CString.Text(Strerror(*(int*)ProcessRuntime.ErrnoLocation())));
    }

    [CExport("mmap"), CExport("mmap64")]
    public static nint Mmap(nint requested, long length, int protection, int flags, int descriptor, long offset)
    {
        if (length <= 0 || offset < 0 || offset % Environment.SystemPageSize != 0 || (flags & 0x10) != 0 || (protection & ~3) != 0)
        {
            ProcessRuntime.Error(22);
            return -1;
        }
        try
        {
            MemoryMappedFileAccess access = (protection & 2) == 0 ? MemoryMappedFileAccess.Read : (flags & 2) != 0 ? MemoryMappedFileAccess.CopyOnWrite : MemoryMappedFileAccess.ReadWrite;
            MemoryMappedFile file;
            if ((flags & 0x20) != 0)
                file = MemoryMappedFile.CreateNew(null, length, MemoryMappedFileAccess.ReadWrite);
            else if (Stdio.StreamFor(descriptor) is FileStream stream)
            {
                stream.Flush();
                file = MemoryMappedFile.CreateFromFile(stream, null, 0, access, HandleInheritability.None, true);
            }
            else
            {
                ProcessRuntime.Error(9);
                return -1;
            }
            try
            {
                MemoryMappedViewAccessor view = file.CreateViewAccessor((flags & 0x20) != 0 ? 0 : offset, length, access);
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                nint address = (nint)(pointer + view.PointerOffset);
                lock (mappings) mappings[address] = new Mapping(file, view, length);
                return address;
            }
            catch { file.Dispose(); throw; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ProcessRuntime.Error(error is UnauthorizedAccessException ? 13 : 22);
            return -1;
        }
    }
    [CExport("munmap")]
    public static int Munmap(nint address, long length)
    {
        Mapping? mapping;
        lock (mappings)
        {
            if (!mappings.TryGetValue(address, out mapping) || mapping.Size != length)
                return ProcessRuntime.Error(22);
            mappings.Remove(address);
        }
        mapping.View.SafeMemoryMappedViewHandle.ReleasePointer();
        mapping.View.Dispose();
        mapping.File.Dispose();
        return 0;
    }
    [CExport("msync")]
    public static int Msync(nint address, long length, int flags)
    {
        lock (mappings)
        {
            if (!mappings.TryGetValue(address, out Mapping? mapping) || length > mapping.Size)
                return ProcessRuntime.Error(22);
            mapping.View.Flush();
            return 0;
        }
    }

    [CExport("isatty")]
    public static int Isatty(int descriptor)
    {
        bool terminal = descriptor switch { 0 => !Console.IsInputRedirected, 1 => !Console.IsOutputRedirected, 2 => !Console.IsErrorRedirected, _ => false };
        if (!terminal) ProcessRuntime.Error(Stdio.StreamFor(descriptor) is null ? 9 : 25);
        return terminal ? 1 : 0;
    }
    [CExport("chmod")]
    public static int Chmod(nint path, int mode) => ChangeMode(CString.Text(path), mode);
    [CExport("fchmod")]
    public static int Fchmod(int descriptor, int mode)
    {
        string? path = Stdio.PathFor(descriptor);
        return path is null ? ProcessRuntime.Error(9) : ChangeMode(path, mode);
    }
    private static int ChangeMode(string path, int mode)
    {
        if (!OperatingSystem.IsLinux()) return ProcessRuntime.Error(38);
        try { File.SetUnixFileMode(path, (UnixFileMode)(mode & 4095)); return 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ProcessRuntime.Error(error is FileNotFoundException ? 2 : 13); }
    }
    [CExport("chown")]
    public static int Chown(nint path, int owner, int group) => owner == -1 && group == -1 ? 0 : ProcessRuntime.Error(38);
    [CExport("fchown")]
    public static int Fchown(int descriptor, int owner, int group) => owner == -1 && group == -1 ? 0 : ProcessRuntime.Error(38);
    [CExport("lstat"), CExport("lstat64")]
    public static int Lstat(nint path, nint result)
    {
        string name = CString.Text(path);
        var info = new FileInfo(name);
        if (info.LinkTarget is null) return Stdio.Stat(path, result);
        NativeMemory.Clear((void*)result, 144);
        *(ulong*)(result + 16) = 1;
        *(int*)(result + 24) = 0xa000 | 511;
        *(long*)(result + 48) = Encoding.UTF8.GetByteCount(info.LinkTarget);
        return 0;
    }
    [CExport("utime")]
    public static int Utime(nint path, nint times)
    {
        if (times == 0) return Utimensat(-100, path, 0, 0);
        long* timestamps = stackalloc long[4] { ((long*)times)[0], 0, ((long*)times)[1], 0 };
        return Utimensat(-100, path, (nint)timestamps, 0);
    }

    [CExport("utimensat")]
    public static int Utimensat(int directory, nint path, nint times, int flags)
    {
        string name = CString.Text(path);
        if (directory != -100 && !Path.IsPathRooted(name) || flags != 0)
            return ProcessRuntime.Error(38);
        try
        {
            DateTime now = DateTime.UtcNow;
            DateTime? Timestamp(int index)
            {
                if (times == 0) return now;
                long seconds = ((long*)times)[index * 2];
                long nanoseconds = ((long*)times)[index * 2 + 1];
                if (nanoseconds == 1073741822) return null;
                if (nanoseconds == 1073741823) return now;
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(nanoseconds / 100);
            }
            DateTime? access = Timestamp(0), modification = Timestamp(1);
            if (access is not null) File.SetLastAccessTimeUtc(name, access.Value);
            if (modification is not null) File.SetLastWriteTimeUtc(name, modification.Value);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ProcessRuntime.Error(error is FileNotFoundException or DirectoryNotFoundException ? 2 : error is UnauthorizedAccessException ? 13 : 22);
        }
    }
    [CExport("mkdir")]
    public static int Mkdir(nint path, int mode)
    {
        string name = CString.Text(path);
        if (Directory.Exists(name) || File.Exists(name)) return ProcessRuntime.Error(17);
        if (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(name)))) return ProcessRuntime.Error(2);
        try
        {
            if (OperatingSystem.IsLinux()) Directory.CreateDirectory(name, (UnixFileMode)(mode & 511));
            else Directory.CreateDirectory(name);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ProcessRuntime.Error(13); }
    }
    [CExport("opendir")]
    public static nint Opendir(nint path)
    {
        string name = CString.Text(path);
        if (!Directory.Exists(name)) { ProcessRuntime.Error(2); return 0; }
        try
        {
            var state = new DirectoryState(name);
            directories[state.Entry] = state;
            return state.Entry;
        }
        catch (UnauthorizedAccessException) { ProcessRuntime.Error(13); return 0; }
    }
    [CExport("readdir"), CExport("readdir64")]
    public static nint Readdir(nint directory)
    {
        if (!directories.TryGetValue(directory, out DirectoryState? state)) { ProcessRuntime.Error(9); return 0; }
        if (!state.Names.MoveNext()) return 0;
        string path = state.Names.Current;
        byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
        NativeMemory.Clear((void*)state.Entry, 280);
        *(ulong*)state.Entry = (ulong)++state.Position;
        *(long*)(state.Entry + 8) = state.Position;
        *(ushort*)(state.Entry + 16) = (ushort)((19 + name.Length + 1 + 7) & ~7);
        *(byte*)(state.Entry + 18) = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ? (byte)10 : Directory.Exists(path) ? (byte)4 : (byte)8;
        name.CopyTo(new Span<byte>((void*)(state.Entry + 19), name.Length));
        return state.Entry;
    }
    [CExport("closedir")]
    public static int Closedir(nint directory)
    {
        if (!directories.Remove(directory, out DirectoryState? state)) return ProcessRuntime.Error(9);
        state.Names.Dispose();
        NativeMemory.Free((void*)state.Entry);
        return 0;
    }
}