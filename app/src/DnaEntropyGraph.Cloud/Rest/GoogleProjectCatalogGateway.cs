using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.CloudResourceManager.v3;
using Google.Apis.CloudResourceManager.v3.Data;
using Google.Apis.Json;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IProjectCatalogGateway"/> over Cloud Resource Manager v3 REST (issue #50). It is never wrapped in a
/// resilience decorator (none exists for it; a test fails if one is added): it routes every HTTP call through <see cref="CloudCallPipeline"/>
/// itself, because project creation is several calls. <c>projects.create</c> is a long-running operation (it returns
/// an operation, and the real error, project limit or organization policy, arrives in the polled one), and wrapping the
/// whole composite in one retry would replay the non-idempotent POST whenever a poll read hit a 429 or a timeout.
/// So the POST is retried alone, each poll read is its own retried call, and one wall-clock deadline covers them all.
/// </summary>
internal sealed class GoogleProjectCatalogGateway : IProjectCatalogGateway
{
    private const string SearchQuery = "state:ACTIVE";
    private const int PageSize = 100;

    private readonly CloudResourceManagerService _service;
    private readonly CloudCallPipeline _pipeline;
    private readonly GoogleCloudOptions _options;

    public GoogleProjectCatalogGateway(CloudResourceManagerService service, CloudCallPipeline pipeline, GoogleCloudOptions options)
    {
        _service = service;
        _pipeline = pipeline;
        _options = options;
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListActiveProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = new List<ProjectSummary>();
        string? pageToken = null;
        do
        {
            var token = pageToken;
            var page = await _pipeline.ExecuteAsync(
                "ProjectCatalog.ListActiveProjects",
                async ct =>
                {
                    var request = _service.Projects.Search();
                    request.Query = SearchQuery;
                    request.PageSize = PageSize;
                    request.PageToken = token;
                    try
                    {
                        return await request.ExecuteAsync(ct).ConfigureAwait(false);
                    }
                    catch (GoogleApiException ex)
                    {
                        throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            projects.AddRange((page.Projects ?? []).Select(Summarize));
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (pageToken is not null);

        return ProjectCatalogOrder.Prefer(projects.Where(p => p.State == ProjectLifecycleState.Active));
    }

    public async Task<ProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        // An id outside Google's grammar can never name a project, and spliced into a resource name ("a/b") it would
        // address a different resource: answer "not visible" without a request.
        if (!ProjectIdGenerator.IsValid(projectId))
        {
            return null;
        }

        try
        {
            var project = await FetchAsync(projectId, cancellationToken).ConfigureAwait(false);
            return project is null ? null : Summarize(project);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 403 && ex.Kind == CloudErrorKind.Permission)
        {
            // Google answers 403 for a project that does not exist as well as for one the account may not see: either
            // way, pick another. A 403 that says the API is off or billing is off is a different problem and must show.
            return null;
        }
    }

    /// <summary>
    /// The project NUMBER (digits), which names the results bucket (<c>deg-&lt;projectNumber&gt;-...</c>): Resource Manager
    /// returns it as <c>name: projects/&lt;number&gt;</c>. A project the account cannot see is a permission error with
    /// the action "pick another project", never a bucket named after nothing.
    /// </summary>
    public async Task<string> GetProjectNumberAsync(string projectId, CancellationToken cancellationToken)
    {
        // Permissive on purpose: legacy and domain-scoped ids ("example.com:proj") are real projects the strict grammar rejects.
        var project = !string.IsNullOrWhiteSpace(projectId) && !projectId.Contains('/') && !projectId.Any(char.IsWhiteSpace) ? await FetchAsync(projectId, cancellationToken).ConfigureAwait(false) : null;
        var name = project?.Name;
        if (name is null || !name.StartsWith("projects/", StringComparison.Ordinal) || name.Length == "projects/".Length)
        {
            throw new CloudOperationException(
                new CloudError(SetupErrorCodes.Permission, 404, "The signed-in account cannot see this Google Cloud project."),
                CloudErrorKind.Permission);
        }

        return name["projects/".Length..];
    }

    private Task<Project?> FetchAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync<Project?>(
            "ProjectCatalog.GetProject",
            async ct =>
            {
                try
                {
                    return await _service.Projects.Get("projects/" + projectId).ExecuteAsync(ct).ConfigureAwait(false);
                }
                catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken);

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

            // The mutating call alone goes through the retrying pipeline. Replaying it after a dropped response is safe
            // only because a 409 on the replay adopts our own project below.
            var operation = await _pipeline.ExecuteAsync(
                "ProjectCatalog.CreateProject",
                async ct =>
                {
                    try
                    {
                        return await _service.Projects.Create(body).ExecuteAsync(ct).ConfigureAwait(false);
                    }
                    catch (GoogleApiException ex)
                    {
                        throw ToCreateException(GoogleApiErrors.FromApiException(ex));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var outcome = await OperationPoller.PollAsync(
                token => PollAsync(operation, projectId, token),
                _options.OperationDeadline,
                cancellationToken,
                _options.Delay,
                _options.TimeProvider).ConfigureAwait(false);

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

    /// <summary>One look at the create operation. The read is its own retried call: a 429 or a timeout here re-reads, it never re-posts.</summary>
    private async Task<OperationPoll<ProjectSummary>> PollAsync(Operation operation, string projectId, CancellationToken cancellationToken)
    {
        // The first look is at the operation create returned; later looks ask Google for it again.
        if (operation.Done != true)
        {
            var name = operation.Name;
            operation = await _pipeline.ExecuteAsync(
                "ProjectCatalog.PollCreateOperation",
                async ct =>
                {
                    try
                    {
                        return await _service.Operations.Get(name).ExecuteAsync(ct).ConfigureAwait(false);
                    }
                    catch (GoogleApiException ex)
                    {
                        throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (operation.Done != true)
        {
            return new OperationPoll<ProjectSummary>(false, null, null);
        }

        if (operation.Error is { } error)
        {
            throw ToCreateException(GoogleApiErrors.FromOperationError(error.Code, error.Message, error.Details));
        }

        if (operation.Response is { Count: > 0 })
        {
            return new OperationPoll<ProjectSummary>(true, SummarizeResponse(operation.Response), null);
        }

        // Done, no error, and no project in the response: do not call that a success with an empty id. Ask for the
        // project we asked for; if it is not there, say so.
        var created = await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (created is null)
        {
            throw new CloudOperationException(
                new CloudError(NoResultCode, null, "The create operation finished without a project."),
                CloudErrorKind.Other);
        }

        return new OperationPoll<ProjectSummary>(true, created, null);
    }

    /// <summary>The <see cref="CloudError.Code"/> of a create operation that finished with neither an error nor a project.</summary>
    internal const string NoResultCode = "OPERATION_NO_RESULT";

    private static CloudOperationException ToCreateException(RpcStatus status) => GoogleApiErrors.ToException(status, kind => kind switch
    {
        CloudErrorKind.Quota => SetupErrorCodes.ProjectQuota,
        CloudErrorKind.OrgPolicy => SetupErrorCodes.OrgPolicyBlock,
        CloudErrorKind.Permission => SetupErrorCodes.Permission,
        CloudErrorKind.ApiDisabled => SetupErrorCodes.ApiDisabled,
        CloudErrorKind.Billing => SetupErrorCodes.NoBilling,
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
