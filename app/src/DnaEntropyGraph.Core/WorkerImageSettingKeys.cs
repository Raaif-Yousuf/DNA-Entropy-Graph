namespace DnaEntropyGraph.Core;

/// <summary>The settings.json keys the worker image choice reads (issue #458) and the Settings Advanced group edits (issue #644). One place, so the page and the run start cannot disagree.</summary>
public static class WorkerImageSettingKeys
{
    /// <summary><c>true</c> allows a <see cref="WorkerImage"/> outside the shipped pinned list. Only a developer sets it, in settings.json: there is deliberately no switch for it in the app.</summary>
    public const string DeveloperMode = "developer_mode";

    /// <summary>A developer's own digest-pinned worker image.</summary>
    public const string WorkerImage = "worker_image";
}
