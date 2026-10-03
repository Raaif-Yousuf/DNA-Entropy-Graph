using System.Runtime.InteropServices;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Windows known-folder lookup (SHGetKnownFolderPath). Downloads can be redirected (OneDrive, a
/// policy), so <c>%USERPROFILE%\Downloads</c> is not reliable. Returns null on any failure; the
/// caller falls back (<c>RunOutputFolders.DefaultParent</c>).
/// </summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string? Downloads()
    {
        try
        {
            var id = DownloadsId;
            var hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path);
            try
            {
                return hr == 0 ? Marshal.PtrToStringUni(path) : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
