using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.Cloudbilling.v1;
using BillingData = Google.Apis.Cloudbilling.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IBillingGateway"/> over Cloud Billing v1 REST (issue #51): <c>projects.getBillingInfo</c>,
/// <c>billingAccounts.list</c> with <c>filter=open=true</c>, and <c>projects.updateBillingInfo</c> (a PUT). Wrapped by
/// <see cref="ResilientBillingGateway"/>; no retry of its own.
/// </summary>
internal sealed partial class GoogleBillingGateway : IBillingGateway
{
    private const string OpenFilter = "open=true";
    private const int PageSize = 100;

    private readonly CloudbillingService _service;

    public GoogleBillingGateway(CloudbillingService service)
    {
        _service = service;
    }

    public async Task<BillingStatus> GetBillingStatusAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var info = await _service.Projects.GetBillingInfo("projects/" + projectId).ExecuteAsync(cancellationToken).ConfigureAwait(false);

            // Google omits billingEnabled when it is false, so an absent value is "off", never "unknown".
            return new BillingStatus(info.BillingEnabled == true, string.IsNullOrEmpty(info.BillingAccountName) ? null : info.BillingAccountName);
        }
        catch (GoogleApiException ex)
        {
            throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
        }
    }

    public async Task<IReadOnlyList<BillingAccountSummary>> ListOpenBillingAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = new List<BillingAccountSummary>();
        string? pageToken = null;
        do
        {
            var request = _service.BillingAccounts.List();
            request.Filter = OpenFilter;
            request.PageSize = PageSize;
            request.PageToken = pageToken;

            BillingData.ListBillingAccountsResponse page;
            try
            {
                page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
            }

            // The filter already asks for open accounts; this keeps a closed one out even if Google ignored it.
            accounts.AddRange((page.BillingAccounts ?? [])
                .Where(a => a.Open == true && !string.IsNullOrEmpty(a.Name))
                .Select(a => new BillingAccountSummary(a.Name, string.IsNullOrWhiteSpace(a.DisplayName) ? a.Name : a.DisplayName)));
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (pageToken is not null);

        return accounts;
    }

    public async Task LinkProjectAsync(string projectId, string billingAccountId, CancellationToken cancellationToken)
    {
        try
        {
            await _service.Projects
                .UpdateBillingInfo(new BillingData.ProjectBillingInfo { BillingAccountName = billingAccountId }, "projects/" + projectId)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            // The reason and kind decide first (the Billing API being off, a billing quota, billing off are not a missing
            // role). THEORY (unverified, no live account): a plain permission 403 that names no other permission means
            // the user lacks billing.resourceAssociations.create on the account; one that names a non-billing
            // permission (for example resourcemanager.projects.createBillingAssignment) is about the project.
            var status = GoogleApiErrors.FromApiException(ex);
            var exception = GoogleApiErrors.ToException(status);
            // USER_PROJECT_DENIED says the caller may not use the project (a serviceusage permission), which no billing
            // admin can fix; its message names the permission after "use", so the regex below cannot see it.
            var projectDenied = status.Reasons.Any(r => string.Equals(r, "USER_PROJECT_DENIED", StringComparison.OrdinalIgnoreCase));
            throw status.HttpStatus == 403 && exception.Kind == CloudErrorKind.Permission && !projectDenied && !NamesNonBillingPermission(status.Message)
                ? new CloudOperationException(new CloudError(SetupErrorCodes.BillingNoPermission, 403, status.Message), CloudErrorKind.Permission)
                : exception;
        }
    }

    private static bool NamesNonBillingPermission(string message)
    {
        var named = NamedPermission().Match(message);
        return named.Success && !named.Groups[1].Value.StartsWith("billing.", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"permission\s+'?([a-z][a-zA-Z0-9]*\.[a-zA-Z0-9.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex NamedPermission();
}
