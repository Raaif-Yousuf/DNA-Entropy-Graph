using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.CloudResourceManager.v3;
using Google.Apis.Iam.v1;
using Google.Apis.Storage.v1;
using CrmData = Google.Apis.CloudResourceManager.v3.Data;
using IamData = Google.Apis.Iam.v1.Data;
using StorageData = Google.Apis.Storage.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IWorkerIdentityGateway"/> (issue #54): IAM v1 for the service account and the custom role,
/// Resource Manager v3 for the project's IAM policy, Cloud Storage v1 for the results bucket's. It is never wrapped in a
/// resilience decorator (a test fails if one is added): every HTTP call goes through <see cref="CloudCallPipeline"/> on
/// its own, so a retry never replays a create.
/// <para>
/// <b>Order.</b> The project is read first (its number names the fallback account, and a project the account cannot see
/// fails before anything is made). Then the account: created, or adopted on a 409, or, when an organization policy
/// forbids service accounts, replaced by the default Compute Engine account (<see cref="WorkerIdentity.NoteCode"/>). Then
/// the role: created, adopted, un-deleted (a deleted role keeps its id for about 7 days and answers a create with 409),
/// or patched when its permissions are not exactly <see cref="WorkerIdentityNames.RolePermissions"/>, then read back
/// ("applied is not present": a patch that does not read back fails with <see cref="SetupErrorCodes.WorkerIdentityNotApplied"/>).
/// Then the two policy bindings, each a read-modify-write.
/// </para>
/// <para>
/// <b>Policy edits.</b> Conditional bindings need policy version 3, so the read asks for it and the write sets it. The
/// write carries the etag it read; a 409 (another writer got in) re-reads and re-applies, five attempts at most. A binding
/// is added only if it is missing (a binding is its role plus its condition), so a second run writes nothing, and a
/// binding or an audit config this code did not add is never removed or changed. A just-created account can be briefly
/// invisible to <c>setIamPolicy</c> (400 "does not exist"): that case alone waits (on the injected clock) and retries,
/// five waits at most.
/// </para>
/// THEORY (unverified, no live project): every wire shape here is Google's documented REST shape, and the wording of the
/// org-policy refusal; docs/ToTest.md carries the rows a real project must prove.
/// </summary>
internal sealed class GoogleIamGateway : IWorkerIdentityGateway
{
    private const int MaxPolicyConflicts = 5;
    private const int MaxVisibilityAttempts = 6;
    private const int PolicyVersion = 3;
    private static readonly TimeSpan VisibilityWait = TimeSpan.FromSeconds(2);

    private readonly IamService _iam;
    private readonly CloudResourceManagerService _resourceManager;
    private readonly StorageService _storage;
    private readonly GoogleProjectCatalogGateway _projects;
    private readonly CloudCallPipeline _pipeline;
    private readonly GoogleCloudOptions _options;

    public GoogleIamGateway(
        IamService iam,
        CloudResourceManagerService resourceManager,
        StorageService storage,
        GoogleProjectCatalogGateway projects,
        CloudCallPipeline pipeline,
        GoogleCloudOptions options)
    {
        _iam = iam;
        _resourceManager = resourceManager;
        _storage = storage;
        _projects = projects;
        _pipeline = pipeline;
        _options = options;
    }

    public async Task<WorkerIdentity> EnsureWorkerIdentityAsync(string projectId, string bucket, CancellationToken cancellationToken)
    {
        var projectNumber = await _projects.GetProjectNumberAsync(projectId, cancellationToken).ConfigureAwait(false);

        var ownEmail = WorkerIdentityNames.ServiceAccountEmail(projectId);
        var identity = await EnsureAccountAsync(projectId, ownEmail, cancellationToken).ConfigureAwait(false)
            ? new WorkerIdentity(ownEmail, null)
            : new WorkerIdentity(WorkerIdentityNames.DefaultComputeAccountEmail(projectNumber), SetupErrorCodes.WorkerDefaultAccount);

        await EnsureRoleAsync(projectId, cancellationToken).ConfigureAwait(false);

        var member = WorkerIdentityNames.Member(identity.ServiceAccountEmail);
        await UpdatePolicyAsync(
            "Iam.BindProjectRole",
            ct => ReadProjectPolicyAsync(projectId, ct),
            policy => AddProjectBinding(policy, WorkerIdentityNames.ProjectRoleName(projectId), member),
            (policy, ct) => WriteProjectPolicyAsync(projectId, policy, ct),
            cancellationToken).ConfigureAwait(false);

        await UpdatePolicyAsync(
            "Iam.BindBucketRole",
            ct => ReadBucketPolicyAsync(bucket, ct),
            policy => AddBucketBinding(policy, member),
            (policy, ct) => WriteBucketPolicyAsync(bucket, policy, ct),
            cancellationToken).ConfigureAwait(false);

        return identity;
    }

    // ------------------------------------------------------------------ the service account

    /// <summary>True when the app's own account exists (created or adopted); false when an organization policy forbids creating one.</summary>
    private async Task<bool> EnsureAccountAsync(string projectId, string email, CancellationToken cancellationToken)
    {
        var body = new IamData.CreateServiceAccountRequest
        {
            AccountId = WorkerIdentityNames.ServiceAccountId,
            ServiceAccount = new IamData.ServiceAccount
            {
                DisplayName = "DNA Entropy Graph worker",
                Description = "Runs the DNA Entropy Graph worker VMs. It can stop or delete only VMs named deg-*, and write to the results bucket.",
            },
        };

        try
        {
            await _pipeline.ExecuteAsync(
                "Iam.CreateServiceAccount",
                async ct =>
                {
                    try
                    {
                        return await _iam.Projects.ServiceAccounts.Create(body, "projects/" + projectId).ExecuteAsync(ct).ConfigureAwait(false);
                    }
                    catch (GoogleApiException ex)
                    {
                        throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                    }
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.OrgPolicy)
        {
            // iam.disableServiceAccountCreation: the default Compute Engine account stands in (same role, same bindings).
            return false;
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 409 || ex.Kind == CloudErrorKind.AlreadyExists)
        {
            // Ours from an earlier run, or from a second PC: adopt it. The read proves it is really there.
            await _pipeline.ExecuteAsync(
                "Iam.GetServiceAccount",
                async ct =>
                {
                    try
                    {
                        return await _iam.Projects.ServiceAccounts.Get($"projects/{projectId}/serviceAccounts/{email}").ExecuteAsync(ct).ConfigureAwait(false);
                    }
                    catch (GoogleApiException getFailure)
                    {
                        throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(getFailure));
                    }
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    // ------------------------------------------------------------------ the custom role

    private async Task EnsureRoleAsync(string projectId, CancellationToken cancellationToken)
    {
        var body = new IamData.CreateRoleRequest
        {
            RoleId = WorkerIdentityNames.RoleId,
            Role = new IamData.Role
            {
                Title = "DNA Entropy Graph worker",
                Description = "Stop or delete the worker VM itself. Granted only for VMs named deg-*.",
                IncludedPermissions = WorkerIdentityNames.RolePermissions.ToList(),
                Stage = "GA",
            },
        };

        try
        {
            await CallAsync(
                "Iam.CreateRole",
                ct => _iam.Projects.Roles.Create(body, "projects/" + projectId).ExecuteAsync(ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 409 || ex.Kind == CloudErrorKind.AlreadyExists)
        {
            await AdoptRoleAsync(WorkerIdentityNames.ProjectRoleName(projectId), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AdoptRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(roleName, cancellationToken).ConfigureAwait(false);

        if (role.Deleted == true)
        {
            // A deleted custom role keeps its id for about 7 days, so a create answers 409: bring it back.
            role = await CallAsync(
                "Iam.UndeleteRole",
                ct => _iam.Projects.Roles.Undelete(new IamData.UndeleteRoleRequest { ETag = role.ETag }, roleName).ExecuteAsync(ct),
                cancellationToken).ConfigureAwait(false);
        }

        if (HasExactPermissions(role))
        {
            return;
        }

        await CallAsync(
            "Iam.PatchRole",
            ct =>
            {
                var patch = _iam.Projects.Roles.Patch(new IamData.Role { IncludedPermissions = WorkerIdentityNames.RolePermissions.ToList() }, roleName);
                patch.UpdateMask = "includedPermissions";
                return patch.ExecuteAsync(ct);
            },
            cancellationToken).ConfigureAwait(false);

        // Applied is not present: the patch is read back and compared, never assumed.
        var applied = await GetRoleAsync(roleName, cancellationToken).ConfigureAwait(false);
        if (applied.Deleted == true || !HasExactPermissions(applied))
        {
            throw NotApplied("Google accepted the worker role but it does not read back with exactly the permissions the app asked for.");
        }
    }

    private Task<IamData.Role> GetRoleAsync(string roleName, CancellationToken cancellationToken)
        => CallAsync("Iam.GetRole", ct => _iam.Projects.Roles.Get(roleName).ExecuteAsync(ct), cancellationToken);

    private static bool HasExactPermissions(IamData.Role role)
        => (role.IncludedPermissions ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(WorkerIdentityNames.RolePermissions);

    // ------------------------------------------------------------------ policy edits

    /// <summary>
    /// The read-modify-write both policies share. <paramref name="apply"/> changes the policy in place and says whether it
    /// changed anything: false means the binding is already there, so nothing is written (a second run adds nothing).
    /// </summary>
    private async Task UpdatePolicyAsync<TPolicy>(
        string operation,
        Func<CancellationToken, Task<TPolicy>> read,
        Func<TPolicy, bool> apply,
        Func<TPolicy, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var conflicts = 0;
        var unseen = 0;
        while (true)
        {
            var policy = await read(cancellationToken).ConfigureAwait(false);
            if (!apply(policy))
            {
                return;
            }

            try
            {
                await write(policy, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CloudOperationException ex) when (ex.Error.HttpStatus == 409)
            {
                // Another writer changed the policy between our read and our write (ABORTED, etag mismatch): read it again.
                if (++conflicts >= MaxPolicyConflicts)
                {
                    throw NotApplied($"{operation}: the policy kept changing under the app ({conflicts} conflicting writes).");
                }
            }
            catch (CloudOperationException ex) when (IsAccountNotVisibleYet(ex))
            {
                // A just-created account can be briefly invisible to setIamPolicy. Wait on the injected clock, then re-read.
                if (++unseen >= MaxVisibilityAttempts)
                {
                    throw NotApplied($"{operation}: Google still reports the worker account as missing after {unseen} tries.");
                }

                await WaitAsync(VisibilityWait * unseen, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsAccountNotVisibleYet(CloudOperationException ex)
        => ex.Error.HttpStatus == 400 && ex.Error.Message?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true;

    private Task WaitAsync(TimeSpan span, CancellationToken cancellationToken)
        => _options.Delay is { } delay ? delay(span, cancellationToken) : Task.Delay(span, _options.TimeProvider, cancellationToken);

    // --- project policy (Resource Manager v3)

    private Task<CrmData.Policy> ReadProjectPolicyAsync(string projectId, CancellationToken cancellationToken)
    {
        var body = new CrmData.GetIamPolicyRequest { Options = new CrmData.GetPolicyOptions { RequestedPolicyVersion = PolicyVersion } };
        return CallAsync("Iam.GetProjectPolicy", ct => _resourceManager.Projects.GetIamPolicy(body, "projects/" + projectId).ExecuteAsync(ct), cancellationToken);
    }

    private Task WriteProjectPolicyAsync(string projectId, CrmData.Policy policy, CancellationToken cancellationToken)
        => CallAsync(
            "Iam.SetProjectPolicy",
            ct => _resourceManager.Projects.SetIamPolicy(new CrmData.SetIamPolicyRequest { Policy = policy }, "projects/" + projectId).ExecuteAsync(ct),
            cancellationToken);

    private static bool AddProjectBinding(CrmData.Policy policy, string role, string member)
    {
        policy.Bindings ??= [];
        var binding = policy.Bindings.FirstOrDefault(b => b.Role == role && b.Condition?.Expression == WorkerIdentityNames.ConditionExpression);
        if (binding is null)
        {
            policy.Bindings.Add(new CrmData.Binding
            {
                Role = role,
                Members = [member],
                Condition = new CrmData.Expr
                {
                    Title = WorkerIdentityNames.ConditionTitle,
                    Description = "The worker may stop or delete only VMs named deg-*.",
                    Expression = WorkerIdentityNames.ConditionExpression,
                },
            });
        }
        else if (binding.Members?.Contains(member) != true)
        {
            binding.Members = [.. binding.Members ?? [], member];
        }
        else
        {
            return false;
        }

        policy.Version = Math.Max(policy.Version ?? 1, PolicyVersion);
        return true;
    }

    // --- bucket policy (Cloud Storage v1)

    private Task<StorageData.Policy> ReadBucketPolicyAsync(string bucket, CancellationToken cancellationToken)
        => CallAsync(
            "Iam.GetBucketPolicy",
            ct =>
            {
                var request = _storage.Buckets.GetIamPolicy(bucket);
                request.OptionsRequestedPolicyVersion = PolicyVersion;
                return request.ExecuteAsync(ct);
            },
            cancellationToken);

    private Task WriteBucketPolicyAsync(string bucket, StorageData.Policy policy, CancellationToken cancellationToken)
        => CallAsync("Iam.SetBucketPolicy", ct => _storage.Buckets.SetIamPolicy(policy, bucket).ExecuteAsync(ct), cancellationToken);

    private static bool AddBucketBinding(StorageData.Policy policy, string member)
    {
        policy.Bindings ??= [];
        var binding = policy.Bindings.FirstOrDefault(b => b.Role == WorkerIdentityNames.BucketRole && b.Condition is null);
        if (binding is null)
        {
            policy.Bindings.Add(new StorageData.Policy.BindingsData { Role = WorkerIdentityNames.BucketRole, Members = [member] });
        }
        else if (binding.Members?.Contains(member) != true)
        {
            binding.Members = [.. binding.Members ?? [], member];
        }
        else
        {
            return false;
        }

        policy.Version = Math.Max(policy.Version ?? 1, PolicyVersion);
        return true;
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>One HTTP call through the pipeline, its Google failure turned into the app's one exception type.</summary>
    private Task<T> CallAsync<T>(string operation, Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            operation,
            async ct =>
            {
                try
                {
                    return await send(ct).ConfigureAwait(false);
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken);

    private static CloudOperationException NotApplied(string detail)
        => new(new CloudError(SetupErrorCodes.WorkerIdentityNotApplied, null, detail), CloudErrorKind.Other);
}
