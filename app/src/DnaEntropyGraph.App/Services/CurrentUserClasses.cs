using DnaEntropyGraph.Core;
using Microsoft.Win32;

namespace DnaEntropyGraph.App.Services;

/// <summary><see cref="IClassesRegistry"/> over <c>HKEY_CURRENT_USER</c> (issue #108): the current user's own keys, never machine-wide, so no administrator prompt and no effect on other users.</summary>
public sealed class CurrentUserClasses : IClassesRegistry
{
    public void SetValue(string subKeyPath, string? valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteSubKeyTree(string subKeyPath) => Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);

    public void DeleteValue(string subKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public bool ValueExists(string subKeyPath, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        return key?.GetValue(valueName) is not null;
    }
}
