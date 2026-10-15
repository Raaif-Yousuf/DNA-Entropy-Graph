using CommunityToolkit.Mvvm.ComponentModel;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Setup;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// One OK/Fix row of the first-run wizard (issue #99): what the step is and why, whether it is green, and for a red row the message
/// and its one action. All text comes from <c>Resources.resw</c> through <see cref="IStringResourceProvider"/> (Hard Rule 13).
/// </summary>
public sealed partial class SetupStepRow : ObservableObject
{
    /// <summary>The plain-voice fallback for a step the wizard cannot finish (Hard Rule 13: every error names an action; this is the second way out).</summary>
    public const string ManualGuideUrl = "https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/blob/main/docs/gcp_setup_manual.md";

    private readonly IStringResourceProvider _strings;

    public SetupStepRow(SetupStep step, IStringResourceProvider strings)
    {
        Step = step;
        _strings = strings;
        Title = strings.GetString(TitleKey(step));
        Why = strings.GetString(WhyKey(step));
        _statusText = strings.GetString("WizardStatus_Pending");
    }

    // A literal per step so the orphan-resource scan sees every key (scripts/check_app_wiring.py).
    private static string TitleKey(SetupStep step) => step switch
    {
        SetupStep.Project => "WizardStep_Project_Title",
        SetupStep.Billing => "WizardStep_Billing_Title",
        SetupStep.Services => "WizardStep_Services_Title",
        SetupStep.Storage => "WizardStep_Storage_Title",
        _ => "WizardStep_Quota_Title",
    };

    private static string WhyKey(SetupStep step) => step switch
    {
        SetupStep.Project => "WizardStep_Project_Why",
        SetupStep.Billing => "WizardStep_Billing_Why",
        SetupStep.Services => "WizardStep_Services_Why",
        SetupStep.Storage => "WizardStep_Storage_Why",
        _ => "WizardStep_Quota_Why",
    };

    public SetupStep Step { get; }

    /// <summary>The row's one button (set by the owning ViewModel, which knows what to do for each action kind).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? ActionCommand { get; set; }

    /// <summary>"Check again": runs this step, and the ones after it, once more.</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? RecheckCommand { get; set; }

    /// <summary>"Skip for now" (GPU allowance only).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? SkipCommand { get; set; }

    public string Title { get; }

    /// <summary>What we are doing, why, and what it costs, in one sentence or two.</summary>
    public string Why { get; }

    public Uri ManualUri { get; } = new(ManualGuideUrl);

    private SetupStatus _status = SetupStatus.Pending;
    private SetupActionKind _actionKind;
    private bool _canSkip;

    /// <summary>Where the row is. Not itself bound: the page binds the flags derived from it (<see cref="IsOk"/>, <see cref="IsFix"/>, <see cref="IsRunning"/>).</summary>
    public SetupStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsOk));
                OnPropertyChanged(nameof(IsFix));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(ShowDoItByHand));
                OnPropertyChanged(nameof(HasAction));
                OnPropertyChanged(nameof(HasLinkAction));
                OnPropertyChanged(nameof(HasCommandAction));
                OnPropertyChanged(nameof(HasRecheck));
                OnPropertyChanged(nameof(HasSkip));
            }
        }
    }

    /// <summary>What the one button does (the ViewModel reads it; the page binds <see cref="HasLinkAction"/> and <see cref="HasCommandAction"/>).</summary>
    public SetupActionKind ActionKind
    {
        get => _actionKind;
        private set
        {
            if (SetProperty(ref _actionKind, value))
            {
                OnPropertyChanged(nameof(HasAction));
                OnPropertyChanged(nameof(HasLinkAction));
                OnPropertyChanged(nameof(HasCommandAction));
                OnPropertyChanged(nameof(HasRecheck));
            }
        }
    }

    /// <summary>The user may leave this step for later (GPU allowance); the page binds <see cref="HasSkip"/>.</summary>
    public bool CanSkip
    {
        get => _canSkip;
        private set
        {
            if (SetProperty(ref _canSkip, value))
            {
                OnPropertyChanged(nameof(HasSkip));
            }
        }
    }

    /// <summary>"OK", "Fix", "Checking...", "Not checked yet" or "Skipped": the badge on the row.</summary>
    [ObservableProperty]
    private string _statusText;

    /// <summary>The message of a red row, or the line of facts of a green one (the bucket, the allowed GPUs); empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _messageText = string.Empty;

    /// <summary>A yellow note under a green row (the worker runs as the project's default account).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _noteText = string.Empty;

    /// <summary>The label of the row's one button; empty when the row has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction), nameof(HasLinkAction), nameof(HasCommandAction), nameof(HasRecheck))]
    private string _actionText = string.Empty;

    /// <summary>The page the link button opens (<see cref="SetupActionKind.OpenLink"/>), else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkAction), nameof(HasCommandAction))]
    private Uri? _linkUri;

    /// <summary>The message for an administrator or project owner, ready for the clipboard (<see cref="SetupActionKind.CopyText"/>).</summary>
    public string RequestText { get; private set; } = string.Empty;

    /// <summary>The last result, for the commands that act on it (the account to link, the code that failed).</summary>
    public SetupStepResult? Last { get; private set; }

    public bool IsOk => Status == SetupStatus.Ok;

    public bool IsFix => Status == SetupStatus.NeedsAction;

    public bool IsRunning => Status == SetupStatus.Running;

    public bool HasMessage => MessageText.Length > 0;

    public bool HasNote => NoteText.Length > 0;

    public bool HasAction => ActionText.Length > 0 && ActionKind != SetupActionKind.None && IsFix;

    public bool HasLinkAction => HasAction && ActionKind == SetupActionKind.OpenLink && LinkUri is not null;

    public bool HasCommandAction => HasAction && !HasLinkAction;

    /// <summary>After a link the user did something on Google's page; "Check again" is the one way to tell the app.</summary>
    public bool HasRecheck => HasLinkAction;

    public bool HasSkip => CanSkip && IsFix;

    public bool ShowDoItByHand => IsFix;

    public void MarkRunning()
    {
        Status = SetupStatus.Running;
        StatusText = _strings.GetString("WizardStatus_Running");
        MessageText = string.Empty;
        NoteText = string.Empty;
        ActionText = string.Empty;
        ActionKind = SetupActionKind.None;
        LinkUri = null;
        CanSkip = false;
    }

    public void MarkPending()
    {
        Status = SetupStatus.Pending;
        StatusText = _strings.GetString("WizardStatus_Pending");
        MessageText = string.Empty;
        NoteText = string.Empty;
        ActionText = string.Empty;
        ActionKind = SetupActionKind.None;
        LinkUri = null;
        CanSkip = false;
    }

    /// <summary>A red row with no failure behind it: the user has to choose (the project list), so the message says what to choose.</summary>
    public void MarkChoose(string messageKey)
    {
        Status = SetupStatus.NeedsAction;
        StatusText = _strings.GetString("WizardStatus_Fix");
        MessageText = _strings.GetString(messageKey);
        NoteText = string.Empty;
        ActionText = string.Empty;
        ActionKind = SetupActionKind.None;
        LinkUri = null;
        CanSkip = false;
    }

    public void MarkSkipped()
    {
        Status = SetupStatus.Skipped;
        StatusText = _strings.GetString("WizardStatus_Skipped");
        MessageText = _strings.GetString("WizardQuota_Skipped");
        ActionText = string.Empty;
        ActionKind = SetupActionKind.None;
        LinkUri = null;
        CanSkip = false;
    }

    /// <summary>Shows what a step found. <paramref name="projectId"/> fills the request texts for an administrator.</summary>
    public void Apply(SetupStepResult result, string projectId)
    {
        Last = result;
        Status = result.Status;
        StatusText = _strings.GetString(result.Status == SetupStatus.Ok ? "WizardStatus_Ok" : "WizardStatus_Fix");
        NoteText = result.NoteCode is { } note ? _strings.GetString(SetupErrorCodes.ResourceKey(note)) : string.Empty;
        CanSkip = result.CanSkip;
        LinkUri = result.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri : null;

        if (result.Status == SetupStatus.Ok)
        {
            MessageText = FactsLine(result);
            ActionText = string.Empty;
            ActionKind = SetupActionKind.None;
            return;
        }

        var message = result.Code is null && result.Step == SetupStep.Quota ? _strings.GetString("WizardQuota_None") : _strings.GetString(MessageKey(result.Code));
        MessageText = result.Step == SetupStep.Quota ? (message + " " + string.Join(" ", result.Tiers.Select(TierLine))).Trim() : message;
        ActionText = result.Action.ResourceKey is { } key ? _strings.GetString(key) : string.Empty;
        ActionKind = result.Action.Kind;
        RequestText = RequestTextFor(result, projectId, message);
    }

    private static string MessageKey(string? code)
        => code == AuthErrorCodes.ProjectInvalid ? AuthErrorCodes.ResourceKey(code) : SetupErrorCodes.ResourceKey(code);

    private string FactsLine(SetupStepResult result) => result.Step switch
    {
        SetupStep.Storage when result.Bucket is { } bucket => string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("WizardStorage_Ok"), bucket),
        SetupStep.Quota => string.Join(" ", result.Tiers.Select(TierLine)),
        _ => string.Empty,
    };

    private string TierLine(QuotaTierResult tier)
    {
        var format = tier.State switch
        {
            QuotaState.Available => _strings.GetString("WizardQuota_Allowed"),
            QuotaState.None => _strings.GetString("WizardQuota_NotYet"),
            _ => _strings.GetString("WizardQuota_Unknown"),
        };
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, tier.TierKey, tier.AllowedRegions);
    }

    private string RequestTextFor(SetupStepResult result, string projectId, string problem)
    {
        if (result.Action.Kind != SetupActionKind.CopyText)
        {
            return string.Empty;
        }

        // The #51 review: the billing request names the account the user chose (or the only one there is), not a bare placeholder.
        if (result.Code == SetupErrorCodes.BillingNoPermission)
        {
            return BillingRequestText.Fill(_strings.GetString("SetupBillingRequestText"), projectId, result.AccountId ?? string.Empty);
        }

        return _strings.GetString("SetupHelpRequestText")
            .Replace("{project}", projectId, StringComparison.Ordinal)
            .Replace("{problem}", problem, StringComparison.Ordinal);
    }
}
