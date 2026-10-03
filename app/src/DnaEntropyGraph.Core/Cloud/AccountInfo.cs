namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// One Google account signed in on this PC. <see cref="Sub"/> is the OpenID subject, the only stable key (an email can
/// change). <see cref="NeedsSignIn"/> is true once Google refused the stored token (<see cref="AuthErrorCodes.SigninExpired"/>).
/// </summary>
public sealed record AccountInfo(string Sub, string Email, bool NeedsSignIn = false);
