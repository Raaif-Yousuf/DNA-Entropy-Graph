namespace DnaEntropyGraph.CloudCli;

/// <summary>
/// A console host for scripts/cloud_gpu_test.ps1 and similar (never
/// user-facing - docs/superpowers/specs Appendix A section 1). The
/// skeleton only proves the project builds and links against Cloud;
/// `resources list` / `bucket describe` / `vm create` subcommands are a
/// follow-up issue once the real gateways exist.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("DnaEntropyGraph.CloudCli: no subcommands implemented yet (solution skeleton, issue #61).");
        return 0;
    }
}
