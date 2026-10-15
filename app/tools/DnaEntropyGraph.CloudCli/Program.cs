namespace DnaEntropyGraph.CloudCli;

/// <summary>
/// A console host for scripts/cloud_gpu_test.ps1 and the dev loop (never user-facing; issue #60): <c>resources list</c>,
/// <c>bucket describe</c>, <c>vm create</c> (only through <c>VmSpec</c>) and <c>vm delete</c> (only <c>deg-</c> VMs with the app
/// label). It signs in as the account the app signed in and never reads a key file. Exit codes: <see cref="CloudCliExitCode"/>.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        return await CloudCliHost.RunAsync(args, Console.Out, Console.Error, appDataDirectory: null, cancel.Token).ConfigureAwait(false);
    }
}
