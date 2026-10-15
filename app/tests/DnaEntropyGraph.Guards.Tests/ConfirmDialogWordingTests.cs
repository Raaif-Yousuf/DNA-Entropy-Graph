using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #644 cold review (1): a confirmation's body must name the buttons the dialog really shows. The shared dialog
/// (DialogService) shows "Continue" and "Cancel"; a body that says "Choose Yes ... or No" points at buttons that do not exist.
/// </summary>
public class ConfirmDialogWordingTests
{
    private static string Value(XDocument doc, string name)
        => doc.Root!.Elements("data").Single(d => d.Attribute("name")?.Value == name).Element("value")!.Value;

    [Fact]
    public void The_reset_confirmation_body_names_the_dialogs_own_button_labels_and_no_yes_or_no()
    {
        var doc = XDocument.Load(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw"));
        var primary = Value(doc, "Dialog_Confirm_Primary");
        var cancel = Value(doc, "Dialog_Confirm_Cancel");

        var body = Value(doc, "AdvancedResetConfirmBody");

        body.ShouldContain(primary);
        body.ShouldContain(cancel);
        body.ShouldNotContain("Yes");
        body.ShouldNotContain(" No ");
    }

    [Fact]
    public void The_worker_image_refusal_puts_the_plain_phrase_before_the_fingerprint_syntax_and_names_one_action()
    {
        var doc = XDocument.Load(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw"));

        var text = Value(doc, "WorkerImageNotPinned");

        text.IndexOf("fingerprint", StringComparison.Ordinal).ShouldBeGreaterThanOrEqualTo(0);
        text.IndexOf("fingerprint", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("@sha256:", StringComparison.Ordinal));
        text.ShouldNotContain(" or clear");
    }
}
