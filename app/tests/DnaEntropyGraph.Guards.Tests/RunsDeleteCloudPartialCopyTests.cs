using System.Xml.Linq;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #262 review round 2: a cloud delete that may have removed some files must never say "nothing was deleted", and each cause names one action
/// (Hard Rule 13). The copy is read from the real Resources.resw, not from the keys.
/// </summary>
public sealed class RunsDeleteCloudPartialCopyTests
{
    private static Dictionary<string, string> Resw()
    {
        var resw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                resw[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return resw;
    }

    [Fact]
    public void Every_cause_of_a_possibly_partial_delete_says_some_files_may_be_gone_and_never_that_nothing_was_deleted()
    {
        var resw = Resw();
        var kinds = Enum.GetValues<CloudErrorKind>();
        kinds.Length.ShouldBeGreaterThan(5, "the scan must see the kinds");

        foreach (var kind in kinds)
        {
            var message = RunsCopy.DeleteCloudPartialMaybe(kind);
            resw.ShouldContainKey(message.Title);
            resw.ShouldContainKey(message.Body);
            var body = resw[message.Body];
            body.Contains("may have been deleted", StringComparison.Ordinal).ShouldBeTrue($"{kind}: the copy must admit some files may be gone");
            body.Contains("nothing was deleted", StringComparison.OrdinalIgnoreCase).ShouldBeFalse($"{kind}: the sweep may have removed some");
            body.ShouldNotContain("—");
            resw[message.Title].ShouldNotContain("—");
        }
    }

    [Fact]
    public void Each_cause_names_exactly_one_action_in_its_own_wording()
    {
        var resw = Resw();

        resw[RunsCopy.DeleteCloudPartialMaybe(CloudErrorKind.Permission).Body].ShouldContain("choose the Google account that owns this storage");
        resw[RunsCopy.DeleteCloudPartialMaybe(CloudErrorKind.OrgPolicy).Body].ShouldContain("Ask your organization's administrator");
        resw[RunsCopy.DeleteCloudPartialMaybe(CloudErrorKind.Billing).Body].ShouldContain("Turn billing back on");
        resw[RunsCopy.DeleteCloudPartialMaybe(CloudErrorKind.ApiDisabled).Body].ShouldContain("run the setup check");
        resw[RunsCopy.DeleteCloudPartialMaybe(CloudErrorKind.Quota).Body].ShouldContain("Wait a few minutes");
        resw[RunsCopy.DeleteCloudPartialMaybe(CloudErrorKind.Network).Body].ShouldContain("Check your internet connection");

        // Other has its own wording: the one thing only it says, and none of the other causes' actions.
        var other = resw[RunsCopy.DeleteCloudPartialMaybe(CloudErrorKind.Other).Body];
        other.ShouldContain("Something unexpected stopped the delete");
        other.ShouldContain("to finish it");
        foreach (var foreign in new[] { "Check your internet connection", "Wait a few minutes", "Turn billing back on", "run the setup check", "Ask your organization's administrator", "choose the Google account" })
        {
            other.ShouldNotContain(foreign);
        }
    }

    [Fact]
    public void The_unknown_count_copy_never_says_files_remain_because_it_does_not_know()
    {
        var resw = Resw();
        var message = RunsCopy.DeleteCloudPartialUnknown;

        resw[message.Title].ShouldNotContain("only partly deleted");
        resw[message.Body].ShouldContain("may");
        resw[message.Body].ShouldNotContain("are still");
        resw[message.Body].ShouldContain("Choose Delete cloud copy again");
        resw[message.Body].ShouldNotContain("—");
    }
}