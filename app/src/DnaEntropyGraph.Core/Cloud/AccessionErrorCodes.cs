using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Maps what is wrong with an NCBI accession run (issue #684), found on this PC before any cloud resource exists (Hard Rule 2), to the run
/// error code recorded for it. The user text lives in Resources.resw under <see cref="RunErrorCodes.ResourceKey"/>; the stored detail is the
/// problem's name only, never the accession.
/// </summary>
public static class AccessionErrorCodes
{
    public static string For(AccessionProblem problem) => problem switch
    {
        AccessionProblem.NotAnAccession or AccessionProblem.RangeIncomplete or AccessionProblem.RangeInvalid => RunErrorCodes.InputAccessionInvalid,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Unmapped AccessionProblem: add it to AccessionErrorCodes."),
    };

    public static string For(AccessionLookupProblem problem) => problem switch
    {
        AccessionLookupProblem.NotFound or AccessionLookupProblem.RangeOutsideRecord => RunErrorCodes.InputAccessionNotFound,
        AccessionLookupProblem.TooLarge => RunErrorCodes.InputAccessionTooLarge,
        AccessionLookupProblem.RateLimited or AccessionLookupProblem.NcbiTrouble or AccessionLookupProblem.TimedOut or AccessionLookupProblem.Unreachable => RunErrorCodes.InputAccessionUnreachable,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Unmapped AccessionLookupProblem: add it to AccessionErrorCodes."),
    };

    public static string DetailFor(AccessionProblem problem) => "AccessionProblem=" + problem;

    public static string DetailFor(AccessionLookupProblem problem) => "AccessionLookupProblem=" + problem;
}
