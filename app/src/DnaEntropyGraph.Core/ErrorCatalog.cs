namespace DnaEntropyGraph.Core;

/// <summary>
/// A single user-facing error: a code, a plain-language title and body, and
/// the action the user can take (Hard Rule 13 - every error names one
/// action). Populated from <c>Strings/en-US/Resources.resw</c> by the
/// issue that implements the real error taxonomy (docs/cloud_design.md);
/// this skeleton only defines the shape.
/// </summary>
public sealed record UserFacingError(string Code, string Title, string Body, string ActionLabel);

/// <summary>Looks up a <see cref="UserFacingError"/> by its error code.</summary>
public interface IErrorCatalog
{
    UserFacingError Describe(string errorCode);
}
