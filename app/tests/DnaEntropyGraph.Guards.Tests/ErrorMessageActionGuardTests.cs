using System.Xml.Linq;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Hard Rule 13 (issue #176): every error code the app can record names one action, and that action is part of the
/// message the user reads. <see cref="RunErrorActions"/> is the one table of which action each code asks for; adding a
/// code to <see cref="RunErrorCodes.All"/> without a row there, or writing a message that does not say its action, fails here.
/// </summary>
public class ErrorMessageActionGuardTests
{
    private static Dictionary<string, string> Resw()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                values[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return values;
    }

    [Fact]
    public void Every_run_error_code_has_a_declared_action_and_the_table_has_no_stale_rows()
    {
        foreach (var code in RunErrorCodes.All)
        {
            RunErrorActions.TryGet(code, out _).ShouldBeTrue($"run error code '{code}' has no row in RunErrorActions: say which one action its message asks for");
        }

        RunErrorActions.Codes.Except(RunErrorCodes.All).ShouldBeEmpty("RunErrorActions has a row for a code that no longer exists");
    }

    [Fact]
    public void Every_run_error_message_says_the_action_its_row_declares_and_has_no_dash()
    {
        var resw = Resw();
        foreach (var code in RunErrorCodes.All)
        {
            var message = resw[RunErrorCodes.ResourceKey(code)];
            RunErrorActions.TryGet(code, out var action).ShouldBeTrue();
            var phrases = RunErrorActions.PhrasesFor(action, resw);

            phrases.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase))
                .ShouldBeTrue($"'{RunErrorCodes.ResourceKey(code)}' is declared as {action} but its message does not say any of: {string.Join(" | ", phrases)}");
            message.ShouldNotContain("—");
            message.ShouldNotContain("–");
        }
    }

    [Fact]
    public void The_local_timeout_message_offers_no_time_limit_option_because_no_page_sets_one()
    {
        // MaxRunDurationMinutes has no UI (only the 240 minute default reaches a local run), so the message must not name it as an action (Rule 13).
        Resw()[RunErrorCodes.ResourceKey(RunErrorCodes.LocalWorkerTimedOut)].ShouldNotContain("time limit", Case.Insensitive);
    }

    [Fact]
    public void The_owner_request_code_declares_the_copy_button()
    {
        RunErrorActions.TryGet(RunErrorCodes.PermissionActAs, out var action).ShouldBeTrue();
        action.ShouldBe(RunErrorAction.CopyOwnerRequest);
    }
}
