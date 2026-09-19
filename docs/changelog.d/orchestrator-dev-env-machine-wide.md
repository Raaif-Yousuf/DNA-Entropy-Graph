- The .NET 10 SDK is now installed machine-wide at `C:\Program Files\dotnet\sdk` (10.0.401), by
  the owner from an elevated prompt, so `docs/onboarding.md` leads with that and keeps the
  per-user `dotnet-install.ps1` route for the unattended case only. The `PATH` and `DOTNET_ROOT`
  overrides that pointed at `%USERPROFILE%\.dotnet` have been removed, so the box has exactly one
  SDK and no ambiguity about which one a build used (#35).
