using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe class Stdio
{
    private sealed class Entry(Stream stream, string? path, int flags, bool owned)
    {
        internal readonly Stream Stream = stream;
        internal readonly string? Path = path;
        internal readonly int Flags = flags;
        internal readonly bool Owned = owned;
        internal nint File;
        internal bool End;
        internal bool Error;
        internal int BufferMode;
        internal readonly Stack<byte> Pushback = [];
    }

    private static readonly Dictionary<int, Entry> descriptors = [];
    private static readonly Dictionary<nint, int> files = [];
    private static readonly Dictionary<string, nint> globals = new(StringComparer.Ordinal);
    private static int nextDescriptor = 3;

    static Stdio()
    {
        descriptors[0] = new Entry(Console.OpenStandardInput(), null, 0, false);
        descriptors[1] = new Entry(Console.OpenStandardOutput(), null, 1, false);
        descriptors[2] = new Entry(Console.OpenStandardError(), null, 1, false);
        foreach ((string name, int descriptor) in new[] { ("stdin", 0), ("stdout", 1), ("stderr", 2) })
        {
            nint address = (nint)NativeMemory.Alloc((nuint)IntPtr.Size);
            *(nint*)address = Attach(descriptor);
            globals[name] = address;
        }
    }

    public static bool HasGlobal(string name) => name is "stdin" or "stdout" or "stderr" or "__llvmnet_stdin" or "__llvmnet_stdout" or "__llvmnet_stderr";
    public static nint Global(string name) => globals[name.StartsWith("__llvmnet_", StringComparison.Ordinal) ? name[10..] : name];
    internal static Stream? StreamFor(int descriptor) => descriptors.TryGetValue(descriptor, out Entry? entry) ? entry.Stream : null;
    internal static string? PathFor(int descriptor) => descriptors.TryGetValue(descriptor, out Entry? entry) ? entry.Path : null;

    private static nint Attach(int descriptor)
    {
        Entry entry = descriptors[descriptor];
        if (entry.File != 0)
            return entry.File;
        entry.File = (nint)NativeMemory.AllocZeroed(224);
        *(int*)(entry.File + 112) = descriptor;
        files[entry.File] = descriptor;
        return entry.File;
    }

    private static int Error(Exception error) => ProcessRuntime.Error(error switch
    {
        FileNotFoundException or DirectoryNotFoundException => 2,
        UnauthorizedAccessException => 13,
        ObjectDisposedException => 9,
        ArgumentException => 22,
        _ => 5
    });

    [CExport("open"), CExport("open64")]
    public static int Open(nint path, int flags, nint arguments)
    {
        string name = CString.Text(path);
        bool create = (flags & 64) != 0;
        bool truncate = (flags & 512) != 0;
        bool exclusive = (flags & 128) != 0;
        FileAccess access = (flags & 3) switch { 0 => FileAccess.Read, 1 => FileAccess.Write, _ => FileAccess.ReadWrite };
        FileMode mode = create ? exclusive ? FileMode.CreateNew : truncate ? FileMode.Create : FileMode.OpenOrCreate : truncate ? FileMode.Truncate : FileMode.Open;
        try
        {
            var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 4096 };
            if (create && arguments != 0 && OperatingSystem.IsLinux())
            {
                VarArgs values = *(VarArgs*)arguments;
                options.UnixCreateMode = (UnixFileMode)(values.Integer() & 511);
            }
            var stream = new FileStream(name, options);
            if ((flags & 1024) != 0)
                stream.Seek(0, SeekOrigin.End);
            int descriptor = nextDescriptor++;
            descriptors.Add(descriptor, new Entry(stream, name, flags, true));
            return descriptor;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Error(error);
        }
    }

    [CExport("fopen"), CExport("fopen64")]
    public static nint Fopen(nint path, nint mode)
    {
        string text = CString.Text(mode);
        int flags = text[0] switch { 'r' => 0, 'w' => 64 | 512 | 1, 'a' => 64 | 1024 | 1, _ => -1 };
        if (flags < 0)
        {
            ProcessRuntime.Error(22);
            return 0;
        }
        if (text.Contains('+'))
            flags = (flags & ~3) | 2;
        if (text.Contains('x'))
            flags |= 128;
        int descriptor = Open(path, flags, 0);
        return descriptor < 0 ? 0 : Attach(descriptor);
    }

    [CExport("fdopen")]
    public static nint Fdopen(int descriptor, nint mode)
    {
        if (!descriptors.ContainsKey(descriptor))
        {
            ProcessRuntime.Error(9);
            return 0;
        }
        return Attach(descriptor);
    }

    [CExport("fileno")]
    public static int Fileno(nint file) => files.TryGetValue(file, out int descriptor) ? descriptor : ProcessRuntime.Error(9);

    [CExport("dup")]
    public static int Dup(int descriptor)
    {
        if (!descriptors.TryGetValue(descriptor, out Entry? entry))
            return ProcessRuntime.Error(9);
        int duplicate = nextDescriptor++;
        descriptors[duplicate] = new Entry(entry.Stream, entry.Path, entry.Flags, entry.Owned);
        return duplicate;
    }

    [CExport("close")]
    public static int Close(int descriptor)
    {
        if (!descriptors.Remove(descriptor, out Entry? entry))
            return ProcessRuntime.Error(9);
        try
        {
            if (entry.Owned && !descriptors.Values.Any(other => ReferenceEquals(other.Stream, entry.Stream)))
                entry.Stream.Dispose();
            else if (entry.Stream.CanWrite)
                entry.Stream.Flush();
            if (entry.File != 0)
            {
                files.Remove(entry.File);
                NativeMemory.Free((void*)entry.File);
            }
            return 0;
        }
        catch (IOException error) { return Error(error); }
    }

    [CExport("fclose")]
    public static int Fclose(nint file) => Close(Fileno(file));

    [CExport("read")]
    public static long Read(int descriptor, nint destination, long count)
    {
        if (!descriptors.TryGetValue(descriptor, out Entry? entry))
            return ProcessRuntime.Error(9);
        if (count < 0)
            return ProcessRuntime.Error(22);
        try
        {
            int length = entry.Stream.Read(new Span<byte>((void*)destination, (int)Math.Min(count, int.MaxValue)));
            entry.End = length == 0 && count != 0;
            return length;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException)
        {
            entry.Error = true;
            return Error(error);
        }
    }

    [CExport("write")]
    public static long Write(int descriptor, nint source, long count)
    {
        if (!descriptors.TryGetValue(descriptor, out Entry? entry))
            return ProcessRuntime.Error(9);
        if (count < 0)
            return ProcessRuntime.Error(22);
        try
        {
            if ((entry.Flags & 1024) != 0)
                entry.Stream.Seek(0, SeekOrigin.End);
            long written = 0;
            while (written < count)
            {
                int length = (int)Math.Min(count - written, int.MaxValue);
                entry.Stream.Write(new ReadOnlySpan<byte>((void*)(source + (nint)written), length));
                written += length;
            }
            if (!entry.Owned || entry.BufferMode == 2)
                entry.Stream.Flush();
            return count;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException)
        {
            entry.Error = true;
            return Error(error);
        }
    }

    [CExport("fread")]
    public static long Fread(nint destination, long size, long count, nint file)
    {
        if (size == 0 || count == 0)
            return 0;
        if (size < 0 || count < 0 || count > long.MaxValue / size)
        {
            ProcessRuntime.Error(75);
            return 0;
        }
        long total = size * count;
        long read = 0;
        Entry entry = descriptors[Fileno(file)];
        while (read < total && entry.Pushback.TryPop(out byte buffered))
            ((byte*)destination)[read++] = buffered;
        while (read < total)
        {
            long chunk = Read(Fileno(file), destination + (nint)read, total - read);
            if (chunk <= 0)
                break;
            read += chunk;
        }
        return read / size;
    }

    [CExport("fwrite")]
    public static long Fwrite(nint source, long size, long count, nint file)
    {
        if (size == 0 || count == 0)
            return 0;
        if (size < 0 || count < 0 || count > long.MaxValue / size)
        {
            ProcessRuntime.Error(75);
            return 0;
        }
        long written = Write(Fileno(file), source, size * count);
        return written < 0 ? 0 : written / size;
    }

    [CExport("lseek"), CExport("lseek64")]
    public static long Seek(int descriptor, long offset, int origin)
    {
        if (!descriptors.TryGetValue(descriptor, out Entry? entry))
            return ProcessRuntime.Error(9);
        if (origin is < 0 or > 2)
            return ProcessRuntime.Error(22);
        try
        {
            long position = entry.Stream.Seek(offset, (SeekOrigin)origin);
            entry.End = false;
            entry.Pushback.Clear();
            return position;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException)
        {
            return error is NotSupportedException ? ProcessRuntime.Error(29) : Error(error);
        }
    }

    [CExport("fseek"), CExport("fseeko"), CExport("fseeko64")]
    public static int Fseek(nint file, long offset, int origin) => Seek(Fileno(file), offset, origin) < 0 ? -1 : 0;
    [CExport("ftell"), CExport("ftello"), CExport("ftello64")]
    public static long Ftell(nint file)
    {
        Entry entry = descriptors[Fileno(file)];
        return entry.Stream.Position - entry.Pushback.Count;
    }
    [CExport("rewind")]
    public static void Rewind(nint file) { Fseek(file, 0, 0); Clearerr(file); }

    [CExport("fflush")]
    public static int Flush(nint file)
    {
        try
        {
            if (file == 0)
            {
                foreach (Entry entry in descriptors.Values)
                    if (entry.Stream.CanWrite)
                        entry.Stream.Flush();
            }
            else
                descriptors[Fileno(file)].Stream.Flush();
            return 0;
        }
        catch (IOException error) { return Error(error); }
    }

    [CExport("ferror")]
    public static int Ferror(nint file) => descriptors[Fileno(file)].Error ? 1 : 0;
    [CExport("feof")]
    public static int Feof(nint file) => descriptors[Fileno(file)].End ? 1 : 0;
    [CExport("clearerr")]
    public static void Clearerr(nint file) { descriptors[Fileno(file)].End = false; descriptors[Fileno(file)].Error = false; }
    [CExport("setvbuf")]
    public static int Setvbuf(nint file, nint buffer, int mode, long size)
    {
        if (mode is < 0 or > 2)
            return ProcessRuntime.Error(22);
        descriptors[Fileno(file)].BufferMode = mode;
        return 0;
    }

    [CExport("setbuf")]
    public static void Setbuf(nint file, nint buffer) => Setvbuf(file, buffer, buffer == 0 ? 2 : 0, 8192);

    [CExport("fputc"), CExport("putc"), CExport("putc_unlocked"), CExport("__overflow")]
    public static int Putc(int value, nint file)
    {
        byte item = (byte)value;
        return Write(Fileno(file), (nint)(&item), 1) == 1 ? item : -1;
    }

    [CExport("fgetc"), CExport("getc"), CExport("getc_unlocked"), CExport("__uflow")]
    public static int Getc(nint file)
    {
        if (descriptors[Fileno(file)].Pushback.TryPop(out byte buffered))
            return buffered;
        byte item;
        return Read(Fileno(file), (nint)(&item), 1) == 1 ? item : -1;
    }

    [CExport("ungetc")]
    public static int Ungetc(int value, nint file)
    {
        if (value == -1) return -1;
        Entry entry = descriptors[Fileno(file)];
        entry.Pushback.Push((byte)value);
        entry.End = false;
        return (byte)value;
    }

    [CExport("getchar")]
    public static int Getchar() => Getc(*(nint*)Global("stdin"));

    [CExport("fgets")]
    public static nint Fgets(nint destination, int count, nint file)
    {
        if (count <= 0)
            return 0;
        int read = 0;
        while (read < count - 1)
        {
            int value = Getc(file);
            if (value < 0)
            {
                if (read == 0 || Ferror(file) != 0)
                    return 0;
                break;
            }
            ((byte*)destination)[read++] = (byte)value;
            if (value == '\n')
                break;
        }
        ((byte*)destination)[read] = 0;
        return destination;
    }

    [CExport("fputs")]
    public static int Fputs(nint text, nint file) => Write(Fileno(file), text, CString.Strlen(text)) < 0 ? -1 : 0;

    [CExport("fprintf"), CExport("vfprintf")]
    public static int Fprintf(nint file, nint format, nint arguments)
    {
        byte[] bytes = Formatting.Format(format, arguments);
        fixed (byte* pointer = bytes)
            return Write(Fileno(file), (nint)pointer, bytes.Length) < 0 ? -1 : bytes.Length;
    }

    [CExport("fstat"), CExport("fstat64")]
    public static int Fstat(int descriptor, nint result)
    {
        if (!descriptors.TryGetValue(descriptor, out Entry? entry))
            return ProcessRuntime.Error(9);
        try
        {
            entry.Stream.Flush();
            return FillStat(entry.Path, result, entry.Path is null ? 0 : entry.Stream.Length);
        }
        catch (IOException error) { return Error(error); }
    }

    [CExport("stat"), CExport("stat64")]
    public static int Stat(nint path, nint result)
    {
        string name = CString.Text(path);
        if (!File.Exists(name) && !Directory.Exists(name))
            return ProcessRuntime.Error(2);
        try { return FillStat(name, result, Directory.Exists(name) ? 0 : new FileInfo(name).Length); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Error(error); }
    }

    private static int FillStat(string? path, nint result, long size)
    {
        NativeMemory.Clear((void*)result, 144);
        *(ulong*)(result + 16) = 1;
        int mode = path is null ? 0x2000 : Directory.Exists(path) ? 0x4000 : 0x8000;
        if (path is not null && OperatingSystem.IsLinux())
            mode |= (int)File.GetUnixFileMode(path);
        *(int*)(result + 24) = mode;
        *(long*)(result + 48) = size;
        *(long*)(result + 56) = 4096;
        *(long*)(result + 64) = (size + 511) / 512;
        if (path is not null)
        {
            *(long*)(result + 72) = new DateTimeOffset(File.GetLastAccessTimeUtc(path)).ToUnixTimeSeconds();
            *(long*)(result + 88) = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
            *(long*)(result + 104) = new DateTimeOffset(File.GetCreationTimeUtc(path)).ToUnixTimeSeconds();
        }
        return 0;
    }

    [CExport("rename")]
    public static int Rename(nint source, nint destination)
    {
        try { File.Move(CString.Text(source), CString.Text(destination), true); return 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Error(error); }
    }
    [CExport("unlink"), CExport("remove")]
    public static int Unlink(nint path)
    {
        string name = CString.Text(path);
        if (!File.Exists(name))
            return ProcessRuntime.Error(2);
        try { File.Delete(name); return 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Error(error); }
    }
    [CExport("rmdir")]
    public static int Rmdir(nint path)
    {
        try { Directory.Delete(CString.Text(path)); return 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Error(error); }
    }
}