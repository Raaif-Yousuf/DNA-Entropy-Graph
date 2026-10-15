namespace DnaEntropyGraph.Presentation.Services;

/// <summary>Asks whether a program file exists, off the caller's thread: a path on an unmounted or slow network share can block for tens of seconds (#641).</summary>
public interface IFileProbe
{
    Task<bool> ExistsAsync(string path);
}

/// <summary>The real probe: <see cref="File.Exists(string?)"/> on the thread pool.</summary>
public sealed class DiskFileProbe : IFileProbe
{
    public Task<bool> ExistsAsync(string path) => Task.Run(() => File.Exists(path));
}
