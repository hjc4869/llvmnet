using System.Text;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>Paths, metadata, directories and links (plan item LC-06). Hard links are not available through .NET (EPERM).</summary>
public static unsafe partial class HostSyscalls
{
    private static int fileCreationMask = 0x12;        // umask 022

    private static long SetFileCreationMask(int mask) => Interlocked.Exchange(ref fileCreationMask, mask & 0x1ff);

    /// <summary>The entry itself when <paramref name="path"/> is a symbolic link (dangling or not), otherwise null.</summary>
    private static FileSystemInfo? SymbolicLinkAt(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? null : info;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>What stat (following links) sees at <paramref name="path"/>; null when nothing exists there.</summary>
    private static FileSystemInfo? Target(string path)
    {
        if (SymbolicLinkAt(path) is FileSystemInfo link)
        {
            FileSystemInfo? final = link.ResolveLinkTarget(returnFinalTarget: true);
            if (final is null || !final.Exists) return null;
            return Directory.Exists(final.FullName) ? new DirectoryInfo(final.FullName) : new FileInfo(final.FullName);
        }
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        return File.Exists(path) ? new FileInfo(path) : null;
    }

    private static ulong InodeOf(string path) => (ulong)(uint)StringComparer.Ordinal.GetHashCode(Path.GetFullPath(path)) + 2;

    private static uint PermissionsOf(FileSystemInfo info, bool directory)
    {
        if (!OperatingSystem.IsWindows()) return (uint)info.UnixFileMode & 0xfff;
        uint permissions = directory ? 0x1edu : 0x1a4u;    // 0755, 0644
        return (info.Attributes & FileAttributes.ReadOnly) != 0 ? permissions & ~0x92u : permissions;
    }

    private static long StatPath(int directory, nint pathPointer, nint buffer, int flags)
    {
        Stat* stat = (Stat*)buffer;
        *stat = default;
        stat->Links = 1;
        stat->User = UserId;
        stat->Group = GroupId;
        stat->BlockSize = 4096;
        string path;
        if ((flags & AT_EMPTY_PATH) != 0 && (pathPointer == 0 || *(byte*)pathPointer == 0))
        {
            OpenFile? file = directory == AT_FDCWD ? null : Lookup(directory);
            if (directory == AT_FDCWD) path = Directory.GetCurrentDirectory();
            else if (file is null) return -EBADF;
            else if (file.Path is null)
            {
                stat->Device = 1;
                stat->Inode = (ulong)file.GetHashCode();
                stat->Mode = file.Kind switch
                {
                    FileKind.Pipe => S_IFIFO | 0x180,
                    FileKind.Console => file.Terminal ? S_IFCHR | 0x190 : S_IFIFO | 0x180,
                    _ when file.Stream is MemoryStream => S_IFREG | 0x124,
                    _ => S_IFCHR | 0x1b6
                };
                if (file.Stream is MemoryStream memory) stat->Size = memory.Length;
                return 0;
            }
            else path = file.Path;
        }
        else path = Resolve(directory, pathPointer);
        if (OpenDevice(path, O_RDONLY) is OpenFile device)
        {
            stat->Device = 1;
            stat->Inode = InodeOf(path);
            stat->Mode = device.Stream is MemoryStream ? S_IFREG | 0x124 : S_IFCHR | 0x1b6;
            if (device.Stream is MemoryStream memory) stat->Size = memory.Length;
            return 0;
        }
        FileSystemInfo? info;
        if ((flags & AT_SYMLINK_NOFOLLOW) != 0 && SymbolicLinkAt(path) is FileSystemInfo link)
        {
            stat->Device = 1;
            stat->Inode = InodeOf(path);
            stat->Mode = S_IFLNK | 0x1ff;
            stat->Size = Encoding.UTF8.GetByteCount(link.LinkTarget!);
            stat->Access = Timespec.From(link.LastAccessTimeUtc);
            stat->Modification = stat->Change = Timespec.From(link.LastWriteTimeUtc);
            return 0;
        }
        info = Target(path);
        if (info is null) return SymbolicLinkAt(path) is null ? Missing(path) : -ENOENT;
        bool isDirectory = info is DirectoryInfo;
        stat->Device = 1;
        stat->Inode = InodeOf(info.FullName);
        stat->Mode = (isDirectory ? S_IFDIR : S_IFREG) | PermissionsOf(info, isDirectory);
        stat->Size = isDirectory ? 4096 : ((FileInfo)info).Length;
        stat->Blocks = (stat->Size + 511) / 512;
        stat->Access = Timespec.From(info.LastAccessTimeUtc);
        stat->Modification = stat->Change = Timespec.From(info.LastWriteTimeUtc);
        return 0;
    }

    private static long Access(int directory, nint pathPointer, int mode)
    {
        if ((mode & ~(R_OK | W_OK | X_OK)) != 0) return -EINVAL;
        string path = Resolve(directory, pathPointer);
        if (OpenDevice(path, O_RDONLY) is not null) return 0;
        FileSystemInfo? info = Target(path);
        if (info is null) return Missing(path);
        uint permissions = PermissionsOf(info, info is DirectoryInfo);
        // The process owns every file it can see (LC-12), so the owner bits decide.
        if ((mode & R_OK) != 0 && (permissions & 0x100) == 0 || (mode & W_OK) != 0 && (permissions & 0x80) == 0 ||
            (mode & X_OK) != 0 && (permissions & 0x40) == 0)
            return -EACCES;
        return 0;
    }

    private static long DirectoryEntries(int descriptor, byte* buffer, int count)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Kind != FileKind.Directory) return -ENOTDIR;
        if (file.Entries is null)
        {
            file.Entries = [(".", DT_DIR), ("..", DT_DIR)];
            foreach (FileSystemInfo entry in new DirectoryInfo(file.Path!).EnumerateFileSystemInfos())
                file.Entries.Add((entry.Name, entry.LinkTarget is not null ? DT_LNK : entry is DirectoryInfo ? DT_DIR : DT_REG));
        }
        int written = 0;
        while (file.EntryIndex < file.Entries.Count)
        {
            (string name, byte type) = file.Entries[file.EntryIndex];
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            int length = (19 + bytes.Length + 1 + 7) & ~7;
            if (written + length > count) break;
            // struct linux_dirent64: d_ino, d_off (the position after this entry), d_reclen, d_type, d_name.
            byte* record = buffer + written;
            new Span<byte>(record, length).Clear();
            *(ulong*)record = InodeOf(Path.Combine(file.Path!, name));
            *(long*)(record + 8) = file.EntryIndex + 1;
            *(ushort*)(record + 16) = (ushort)length;
            record[18] = type;
            bytes.CopyTo(new Span<byte>(record + 19, bytes.Length));
            written += length;
            file.EntryIndex++;
        }
        return written == 0 && file.EntryIndex < file.Entries.Count ? -EINVAL : written;
    }

    private static long MakeDirectory(int directory, nint pathPointer, int mode)
    {
        string path = Resolve(directory, pathPointer);
        if (File.Exists(path) || Directory.Exists(path) || SymbolicLinkAt(path) is not null) return -EEXIST;
        if (!Directory.Exists(Path.GetDirectoryName(path))) return Missing(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, (UnixFileMode)(mode & 0x1ff & ~fileCreationMask));
        return 0;
    }

    private static long Unlink(int directory, nint pathPointer, int flags)
    {
        string path = Resolve(directory, pathPointer);
        bool link = SymbolicLinkAt(path) is not null;
        if ((flags & AT_REMOVEDIR) != 0)
        {
            if (link || File.Exists(path)) return -ENOTDIR;
            if (!Directory.Exists(path)) return Missing(path);
            if (Directory.EnumerateFileSystemEntries(path).Any()) return -ENOTEMPTY;
            Directory.Delete(path);
            return 0;
        }
        if (link) { File.Delete(path); return 0; }
        if (Directory.Exists(path)) return -EISDIR;
        if (!File.Exists(path)) return Missing(path);
        File.Delete(path);
        return 0;
    }

    private static long Rename(int fromDirectory, nint from, int toDirectory, nint to, int flags)
    {
        const int NoReplace = 1;
        if ((flags & ~NoReplace) != 0) return -EINVAL;
        string source = Resolve(fromDirectory, from), destination = Resolve(toDirectory, to);
        bool sourceLink = SymbolicLinkAt(source) is not null, destinationLink = SymbolicLinkAt(destination) is not null;
        bool sourceDirectory = !sourceLink && Directory.Exists(source);
        if (!sourceLink && !sourceDirectory && !File.Exists(source)) return Missing(source);
        bool destinationDirectory = !destinationLink && Directory.Exists(destination);
        bool destinationExists = destinationLink || destinationDirectory || File.Exists(destination);
        if (destinationExists && (flags & NoReplace) != 0) return -EEXIST;
        if (Path.GetFullPath(source) == Path.GetFullPath(destination)) return 0;
        if (!Directory.Exists(Path.GetDirectoryName(destination))) return Missing(destination);
        if (sourceDirectory)
        {
            if (destinationExists && !destinationDirectory) return -ENOTDIR;
            if (Path.GetFullPath(destination).StartsWith(Path.GetFullPath(source) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return -EINVAL;
            if (destinationDirectory)
            {
                if (Directory.EnumerateFileSystemEntries(destination).Any()) return -ENOTEMPTY;
                Directory.Delete(destination);
            }
            Directory.Move(source, destination);
            return 0;
        }
        if (destinationDirectory) return -EISDIR;
        if (destinationLink) File.Delete(destination);
        File.Move(source, destination, overwrite: true);
        return 0;
    }

    private static long SymbolicLink(nint targetPointer, int directory, nint pathPointer)
    {
        string target = Text(targetPointer), path = Resolve(directory, pathPointer);
        if (target.Length == 0) return -ENOENT;
        if (File.Exists(path) || Directory.Exists(path) || SymbolicLinkAt(path) is not null) return -EEXIST;
        if (!Directory.Exists(Path.GetDirectoryName(path))) return Missing(path);
        File.CreateSymbolicLink(path, target);
        return 0;
    }

    private static long ReadLink(int directory, nint pathPointer, byte* buffer, long size)
    {
        if (size <= 0) return -EINVAL;
        string path = Resolve(directory, pathPointer);
        string? target = path == "/proc/self/exe" ? Environment.ProcessPath : SymbolicLinkAt(path)?.LinkTarget;
        if (target is null) return File.Exists(path) || Directory.Exists(path) ? -EINVAL : Missing(path);
        byte[] bytes = Encoding.UTF8.GetBytes(target);
        int length = (int)Math.Min(bytes.Length, size);
        bytes.AsSpan(0, length).CopyTo(new Span<byte>(buffer, length));
        return length;
    }

    /// <summary>The path behind a descriptor (null path) or a path argument; null with <paramref name="error"/> set otherwise.</summary>
    private static string? PathOf(int directory, nint pathPointer, out long error)
    {
        error = 0;
        if (pathPointer != 0) return Resolve(directory, pathPointer);
        OpenFile? file = Lookup(directory);
        if (file?.Path is null) error = file is null ? -EBADF : -EINVAL;
        return file?.Path;
    }

    private static long ChangeMode(int directory, nint pathPointer, int mode)
    {
        string? path = PathOf(directory, pathPointer, out long error);
        if (path is null) return error;
        if (Target(path) is not FileSystemInfo info) return Missing(path);
        if (OperatingSystem.IsWindows())
            info.Attributes = (mode & 0x80) == 0 ? info.Attributes | FileAttributes.ReadOnly : info.Attributes & ~FileAttributes.ReadOnly;
        else File.SetUnixFileMode(info.FullName, (UnixFileMode)(mode & 0xfff));
        return 0;
    }

    /// <summary>Ownership cannot change: every file belongs to the one user and group (LC-12).</summary>
    private static long ChangeOwner(int directory, nint pathPointer, int user, int group)
    {
        string? path = PathOf(directory, pathPointer, out long error);
        if (path is null) return error;
        if (Target(path) is null && SymbolicLinkAt(path) is null) return Missing(path);
        return user is -1 or UserId && group is -1 or GroupId ? 0 : -EPERM;
    }

    private static long TruncatePath(nint pathPointer, long length)
    {
        string path = Resolve(AT_FDCWD, pathPointer);
        if (length < 0) return -EINVAL;
        if (Directory.Exists(path)) return -EISDIR;
        if (!File.Exists(path)) return Missing(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(length);
        return 0;
    }

    private static long CurrentDirectory(byte* buffer, long size)
    {
        byte[] path = Encoding.UTF8.GetBytes(Directory.GetCurrentDirectory().Replace('\\', '/'));
        if (path.Length + 1 > size) return -ERANGE;
        path.CopyTo(new Span<byte>(buffer, path.Length));
        buffer[path.Length] = 0;
        return path.Length + 1;
    }

    private static long ChangeDirectory(nint pathPointer)
    {
        string path = Resolve(AT_FDCWD, pathPointer);
        if (Target(path) is not FileSystemInfo info) return Missing(path);
        if (info is not DirectoryInfo) return -ENOTDIR;
        Directory.SetCurrentDirectory(path);
        return 0;
    }

    private static long ChangeDirectory(int descriptor)
    {
        OpenFile? file = Lookup(descriptor);
        if (file is null) return -EBADF;
        if (file.Kind != FileKind.Directory) return -ENOTDIR;
        Directory.SetCurrentDirectory(file.Path!);
        return 0;
    }

    /// <summary>utimes (timeval pairs) and utimensat (timespec pairs, UTIME_NOW, UTIME_OMIT); a null path names the descriptor.</summary>
    private static long SetTimes(int directory, nint pathPointer, long* times, bool microseconds, int flags)
    {
        string? path = PathOf(directory, pathPointer, out long error);
        if (path is null) return error;
        if (Target(path) is not FileSystemInfo info) return Missing(path);
        DateTime now = DateTime.UtcNow;
        for (int index = 0; index < 2; index++)
        {
            long seconds = times == null ? 0 : times[index * 2], fraction = times == null ? UTIME_NOW : times[index * 2 + 1];
            if (microseconds) fraction = fraction is < 0 or >= 1_000_000 ? -1 : fraction * 1000;
            if (fraction == UTIME_OMIT) continue;
            if (fraction != UTIME_NOW && fraction is < 0 or >= 1_000_000_000) return -EINVAL;
            DateTime time = fraction == UTIME_NOW ? now : DateTime.UnixEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + fraction / 100);
            if (index == 0) info.LastAccessTimeUtc = time;
            else info.LastWriteTimeUtc = time;
        }
        return 0;
    }

    /// <summary>statfs and fstatfs from the .NET drive that holds the path; the file-system type is reported as 0 (unknown).</summary>
    private static long FileSystemStatusOf(int directory, nint pathPointer, FileSystemStatus* status)
    {
        string? path = PathOf(directory, pathPointer, out long error);
        if (path is null) return error == -EINVAL ? FillUnknown(status) : error;
        if (Target(path) is null) return Missing(path);
        DriveInfo? drive = DriveInfo.GetDrives().Where(candidate => path.StartsWith(candidate.RootDirectory.FullName, StringComparison.Ordinal))
            .MaxBy(candidate => candidate.RootDirectory.FullName.Length);
        FillUnknown(status);
        try
        {
            if (drive is { IsReady: true })
            {
                status->Blocks = drive.TotalSize / status->BlockSize;
                status->FreeBlocks = drive.TotalFreeSpace / status->BlockSize;
                status->AvailableBlocks = drive.AvailableFreeSpace / status->BlockSize;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        return 0;

        static long FillUnknown(FileSystemStatus* status)
        {
            *status = default;
            status->BlockSize = status->FragmentSize = 4096;
            status->NameLength = 255;
            status->Files = status->FreeFiles = 1 << 20;
            return 0;
        }
    }
}
