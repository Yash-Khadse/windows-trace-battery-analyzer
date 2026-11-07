using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wtba.Collector;

/// <summary>
/// Handle-based file operations (read-only queries plus one delete). Nothing here touches system settings.
/// The delete opens the file itself WITHOUT following a final-component symlink, verifies through the handle where the
/// file really lives, and only then marks that very handle for deletion, so swapping a folder for a junction after an
/// earlier path check cannot redirect the delete.
/// </summary>
public static class NativeFs
{
    private const uint DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400, FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const int FileDispositionInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref int info, uint size);

    private static string FinalPath(SafeFileHandle h)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) throw new IOException("cannot resolve final path");
        return Strip(sb.ToString());
    }

    internal static string Strip(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + p[8..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p[4..];
        return p;
    }

    /// <summary>Real (symlink/junction-resolved) path of an existing directory.</summary>
    public static string? RealDirectoryPath(string dir)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var h = CreateFileW(dir, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return null;
        return FinalPath(h).TrimEnd('\\');
    }

    /// <summary>
    /// Deletes the single regular file at <paramref name="path"/> only if the opened handle is not a reparse point, not a
    /// directory, and its real final path equals <paramref name="expectedRealPath"/>. Throws otherwise (nothing deleted).
    /// </summary>
    public static void DeleteVerified(string path, string expectedRealPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("verified delete requires Windows");
        using var h = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException("cannot open file for delete (win32 " + Marshal.GetLastWin32Error() + ")");
        if (!GetFileInformationByHandle(h, out var info)) throw new IOException("cannot query file");
        if ((info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0)
            throw new UnauthorizedAccessException("handle is a reparse point or directory; refusing delete");
        var real = FinalPath(h);
        if (!string.Equals(real, expectedRealPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"real path '{real}' differs from expected '{expectedRealPath}'; refusing delete");
        int del = 1; // FILE_DISPOSITION_INFO.DeleteFile = TRUE: file is removed when this handle closes
        if (!SetFileInformationByHandle(h, FileDispositionInfo, ref del, sizeof(int)))
            throw new IOException("cannot mark file for deletion (win32 " + Marshal.GetLastWin32Error() + ")");
    }
}
