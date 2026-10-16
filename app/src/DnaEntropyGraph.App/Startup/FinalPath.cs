using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #264 review: the real location of a folder, with every junction and symbolic link followed and every 8.3 short name expanded
/// (<c>GetFinalPathNameByHandle</c>), so the "is the data folder inside the installer's folder" check compares where the folders
/// really are and not how they are spelled. A path that does not exist yet is resolved through its nearest existing ancestor.
/// </summary>
internal static class FinalPath
{
    private const uint FileShareAll = 0x7; // read | write | delete: never block another process
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // required to open a directory

    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var existing = full;
        var tail = new Stack<string>();
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent))
            {
                return full;
            }

            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = Query(existing) ?? existing;
        foreach (var part in tail)
        {
            resolved = Path.Combine(resolved, part);
        }

        return resolved;
    }

    private static string? Query(string directory)
    {
        try
        {
            using var handle = CreateFileW(directory, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length > buffer.Capacity)
            {
                return null;
            }

            var result = buffer.ToString();
            if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + result[8..];
            }

            return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
