using System.Data.Common;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.LocalEngine;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Switching the Google Cloud project without signing in again (issue #260). The sign-in belongs to the account, so a switch only
/// changes which project the account points at (<see cref="IGcpAccount.SelectProjectAsync"/>) and reads back what was remembered for
/// that project.
/// </summary>
/// <remarks>
/// A switch is never refused because of a run. Since #830 every call on an existing run (reconcile, stop, delete, cancel, keep-alive expiry, re-download) is
/// made in the run's OWN project and account, read from its row (<see cref="RunScope"/>), never from the selection; the selection is only read when a NEW run
/// starts (<see cref="IGcpAccount.SelectedScope"/>). So a run in project A keeps going, and ends, in A after the account points at B. (The first version of this
/// feature refused a switch while any run was unfinished, because then the calls that ended a VM looked in the selected project; that guard also had a window between
/// its check and the selection, and could not see a finished run whose VM was still up. It is gone with its cause.)
/// Order of work: check Google can open the project, remember it, and only then <see cref="IGcpAccount.SelectProjectAsync"/>, so a failure before it leaves the
/// account where it was.
/// </remarks>
public sealed class ProjectSwitcher
{
    private readonly IGcpAccount _account;
    private readonly IProjectSettingsStore _store;
    private readonly IRunRepository _runs;
    private readonly IProjectSetupGateway _setup;

    public ProjectSwitcher(IGcpAccount account, IProjectSettingsStore store, IRunRepository runs, IProjectSetupGateway setup)
    {
        _account = account;
        _store = store;
        _runs = runs;
        _setup = setup;
    }

    /// <summary>
    /// The projects the current account can switch to: the ones it chose, the ones its own runs were made in (a row with no recorded account counts for every account,
    /// as on the Runs page, issue #112), and the current one, each with what is remembered for it. Throws what the store or the run history throws (the card catches it).
    /// </summary>
    public async Task<IReadOnlyList<ProjectSettings>> ListAsync(CancellationToken cancellationToken)
    {
        var sub = _account.CurrentAccount?.Sub;
        var known = (await _store.GetKnownAsync(sub, cancellationToken).ConfigureAwait(false)).ToDictionary(p => p.ProjectId, StringComparer.Ordinal);

        var rows = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var fromRuns = rows
            .Where(r => !RunTargets.IsLocalRecord(r.Target) && !string.IsNullOrWhiteSpace(r.ProjectId) && (r.AccountSub is null || r.AccountSub == sub))
            .Select(r => r.ProjectId!)
            .Distinct(StringComparer.Ordinal);
        foreach (var projectId in fromRuns)
        {
            if (!known.ContainsKey(projectId))
            {
                known[projectId] = await _store.GetAsync(projectId, cancellationToken).ConfigureAwait(false) ?? new ProjectSettings(projectId);
            }
        }

        if (_account.SelectedProjectId is { Length: > 0 } current)
        {
            // A project chosen in the wizard (or before the AccountProjects table existed) was never written for this account. Record it now, so it stays in the list after a
            // switch away from it. Best effort: the list still shows it as the current project when the store cannot be written.
            await TryRecordAsync(current, sub, cancellationToken).ConfigureAwait(false);
            if (!known.ContainsKey(current))
            {
                known[current] = await _store.GetAsync(current, cancellationToken).ConfigureAwait(false) ?? new ProjectSettings(current);
            }
        }

        return known.Values.OrderBy(p => p.ProjectId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Points the current account at <paramref name="projectId"/> and returns what is remembered for it. Throws <see cref="AccountAuthException"/>, each one
    /// leaving the current project as it was: <see cref="AuthErrorCodes.ProjectInvalid"/> for an illegal id, <see cref="AuthErrorCodes.ProjectUnreachable"/> when
    /// Google would not open the project for this account, <see cref="AuthErrorCodes.NetworkUnavailable"/> when Google could not be asked,
    /// <see cref="AuthErrorCodes.ProjectSaveFailed"/> when the local store failed, and whatever sign-in code the account itself raises (an expired sign-in stays
    /// an expired sign-in). Cancellation is not swallowed.
    /// </summary>
    public async Task<ProjectSettings> SwitchAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!ProjectIdGenerator.IsValid(projectId))
        {
            throw new AccountAuthException(AuthErrorCodes.ProjectInvalid, "the project id is not a legal Google project id");
        }

        // The account is read ONCE. The check, the remembering and the selection must all be about the same account: SelectProjectAsync applies to whichever account is
        // active at that moment, so a change of account in between would otherwise point the NEW account at a project that was checked for the old one.
        var sub = _account.CurrentAccount?.Sub;
        var leaving = _account.SelectedProjectId;
        if (!string.Equals(projectId, leaving, StringComparison.Ordinal))
        {
            await VerifyAccessAsync(projectId, sub, cancellationToken).ConfigureAwait(false);
            EnsureSameAccount(sub);
        }

        ProjectSettings settings;
        try
        {
            if (leaving is { Length: > 0 } && !string.Equals(projectId, leaving, StringComparison.Ordinal))
            {
                // The project being LEFT is recorded for the account too (the wizard never wrote it), so it is still offered after the switch.
                await _store.EnsureAsync(leaving, sub, cancellationToken).ConfigureAwait(false);
            }

            await _store.EnsureAsync(projectId, sub, cancellationToken).ConfigureAwait(false);
            settings = await _store.GetAsync(projectId, cancellationToken).ConfigureAwait(false) ?? new ProjectSettings(projectId);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbException or UnauthorizedAccessException)
        {
            throw new AccountAuthException(AuthErrorCodes.ProjectSaveFailed, "the project could not be remembered", ex);
        }

        // Last look before the one call that acts on "whoever is active". (A change in the instant between this look and the call cannot be seen from here: closing it
        // needs the account service to take the expected account, which is its own change.)
        EnsureSameAccount(sub);
        await _account.SelectProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    /// <summary>Aborts the switch, selecting nothing, when the active account is no longer the one the switch started with.</summary>
    private void EnsureSameAccount(string? sub)
    {
        if (!string.Equals(_account.CurrentAccount?.Sub, sub, StringComparison.Ordinal))
        {
            throw new AccountAuthException(AuthErrorCodes.AccountChangedDuringSwitch, "the active account changed while the project switch was in progress");
        }
    }

    /// <summary>Records <paramref name="projectId"/> as one of the account's projects; a store that cannot be written is not a reason to fail the caller.</summary>
    private async Task TryRecordAsync(string projectId, string? sub, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sub))
        {
            return;
        }

        try
        {
            await _store.EnsureAsync(projectId, sub, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"project not recorded for the account: {ex.GetType().Name}");
        }
    }

    /// <summary>Asks Google, as account <paramref name="sub"/>, whether the TARGET project can be opened. Not the selected one: the selection is exactly what is about to change.</summary>
    private async Task VerifyAccessAsync(string projectId, string? sub, CancellationToken cancellationToken)
    {
        ProjectLifecycleState state;
        try
        {
            state = await _setup.GetProjectStateAsync(new CloudScope(projectId, sub), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (IsNoAnswer(ex))
        {
            throw new AccountAuthException(AuthErrorCodes.NetworkUnavailable, "Google could not be reached to check the project", ex);
        }
        catch (CloudOperationException ex)
        {
            throw new AccountAuthException(AuthErrorCodes.ProjectUnreachable, "Google would not open the project", ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or AccountAuthException))
        {
            // An unexpected failure of the check is still one named message with one action, never a raw exception on the Settings page.
            throw new AccountAuthException(AuthErrorCodes.ProjectUnreachable, "the project could not be checked", ex);
        }

        if (state != ProjectLifecycleState.Active)
        {
            throw new AccountAuthException(AuthErrorCodes.ProjectUnreachable, "the project is not active");
        }
    }

    /// <summary>Google never answered: a network failure, or a build with no Google layer behind it (the same "no answer" the reconciler defers on). Not the project's fault.</summary>
    private static bool IsNoAnswer(CloudOperationException ex)
        => ex.Kind == CloudErrorKind.Network || ex.Error.Code == RunErrorCodes.NotConnectedGatewayCode;
}
