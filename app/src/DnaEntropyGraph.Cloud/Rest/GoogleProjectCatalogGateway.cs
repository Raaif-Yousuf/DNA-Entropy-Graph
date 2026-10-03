using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.CloudResourceManager.v3;
using Google.Apis.CloudResourceManager.v3.Data;
using Google.Apis.Json;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IProjectCatalogGateway"/> over Cloud Resource Manager v3 REST (issue #50). It does no retry of
/// its own: it is wrapped by <see cref="ResilientProjectCatalogGateway"/>, which routes it through
/// <see cref="CloudCallPipeline"/>. Project creation is a long-running operation: <c>projects.create</c> returns an
/// operation, and the real error (project limit, organization policy) arrives in the polled one.
/// </summary>
internal sealed class GoogleProjectCatalogGateway : IProjectCatalogGateway
{
    private const string SearchQuery = "state:ACTIVE";
    private const int PageSize = 100;

    private readonly CloudResourceManagerService _service;
    private readonly GoogleCloudOptions _options;

    public GoogleProjectCatalogGateway(CloudResourceManagerService service, GoogleCloudOptions options)
    {
        _service = service;
        _options = options;
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListActiveProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = new List<ProjectSummary>();
        string? pageToken = null;
        do
        {
            var request = _service.Projects.Search();
            request.Query = SearchQuery;
            request.PageSize = PageSize;
            request.PageToken = pageToken;

            SearchProjectsResponse page;
            try
            {
                page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
            }

            projects.AddRange((page.Projects ?? []).Select(Summarize));
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (pageToken is not null);

        return ProjectCatalogOrder.Prefer(projects.Where(p => p.State == ProjectLifecycleState.Active));
    }

    public async Task<ProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            return Summarize(await _service.Projects.Get("projects/" + projectId).ExecuteAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (GoogleApiException ex)
        {
            var exception = GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));

            // Google answers 403 for a project that does not exist as well as for one the account may not see: either
            // way, pick another. A 403 that says the API is off or billing is off is a different problem and must show.
            if (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden && exception.Kind == CloudErrorKind.Permission)
            {
                return null;
            }

            throw exception;
        }
    }

    public async Task<ProjectSummary> CreateProjectAsync(string projectId, string displayName, string installationId, CancellationToken cancellationToken)
    {
        try
        {
            var body = new Project
            {
                ProjectId = projectId,
                DisplayName = displayName,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = VmSpec.AppLabelValue,
                    ["installation-id"] = installationId,
                },
            };

            Operation operation;
            try
            {
                operation = await _service.Projects.Create(body).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                throw ToCreateException(GoogleApiErrors.FromApiException(ex));
            }

            var outcome = await OperationPoller.PollAsync(
                token => PollAsync(operation, token),
                _options.OperationDeadline,
                cancellationToken,
                _options.Delay).ConfigureAwait(false);

            if (!outcome.Success)
            {
                throw new CloudOperationException(outcome.Error!, CloudErrorClassifier.Classify(outcome.Error!));
            }

            return outcome.Value!;
        }
        catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.AlreadyExists)
        {
            // A replay after a dropped connection: the first attempt already made it. Adopt our own project, never somebody else's.
            var existing = await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (existing is { IsAppProject: true, State: ProjectLifecycleState.Active })
            {
                return existing;
            }

            throw;
        }
    }

    private async Task<OperationPoll<ProjectSummary>> PollAsync(Operation operation, CancellationToken cancellationToken)
    {
        // The first look is at the operation create returned; later looks ask Google for it again.
        if (operation.Done != true)
        {
            try
            {
                operation = await _service.Operations.Get(operation.Name).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
            }
        }

        if (operation.Done != true)
        {
            return new OperationPoll<ProjectSummary>(false, null, null);
        }

        if (operation.Error is { } error)
        {
            throw ToCreateException(GoogleApiErrors.FromOperationError(error.Code, error.Message, error.Details));
        }

        return new OperationPoll<ProjectSummary>(true, SummarizeResponse(operation.Response), null);
    }

    private static CloudOperationException ToCreateException(RpcStatus status) => GoogleApiErrors.ToException(status, kind => kind switch
    {
        CloudErrorKind.Quota => SetupErrorCodes.ProjectQuota,
        CloudErrorKind.OrgPolicy => SetupErrorCodes.OrgPolicyBlock,
        _ => null,
    });

    private static ProjectSummary Summarize(Project project) => new(
        project.ProjectId ?? string.Empty,
        string.IsNullOrWhiteSpace(project.DisplayName) ? project.ProjectId ?? string.Empty : project.DisplayName,
        string.Equals(project.State, "ACTIVE", StringComparison.Ordinal) ? ProjectLifecycleState.Active : ProjectLifecycleState.Other,
        project.Labels is { } labels && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue);

    /// <summary>The finished operation's <c>response</c> is a Project in an untyped bag; round-trip it through JSON to read it.</summary>
    private static ProjectSummary SummarizeResponse(IDictionary<string, object>? response)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(response ?? new Dictionary<string, object>());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var labels = new Dictionary<string, string>();
        if (root.TryGetProperty("labels", out var labelElement) && labelElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelElement.EnumerateObject())
            {
                labels[label.Name] = label.Value.GetString() ?? string.Empty;
            }
        }

        return Summarize(new Project { ProjectId = Text("projectId"), DisplayName = Text("displayName"), State = Text("state"), Labels = labels });
    }
}
