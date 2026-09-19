- Documented the .NET 10 SDK install route that actually works unattended on the dev laptop.
  `winget install Microsoft.DotNet.SDK.10` fails with exit code 1602 because the machine-wide
  installer wants elevation and a non-interactive session cannot answer the UAC prompt;
  `dotnet-install.ps1 -InstallDir "$env:USERPROFILE\.dotnet"` needs no administrator. The
  shared host on `PATH` only finds SDKs beside itself, so a per-user SDK stays invisible until
  `PATH` and `DOTNET_ROOT` prefer it, which reads exactly like a failed install (#35).
- MEASURED 2026-09-19: an unpackaged, self-contained WinUI 3 app on .NET 10.0.401 with
  Microsoft.WindowsAppSDK 1.8.250916003 restores, compiles its XAML and builds with zero
  warnings using only the `dotnet` CLI, with no Visual Studio workload installed. The evidence
  and the four things still unverified are recorded on #35.
