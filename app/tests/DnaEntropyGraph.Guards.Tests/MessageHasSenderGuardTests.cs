using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #1086, wired-to-nothing: <c>RunProgressChangedMessage</c> had a receiver (<c>RunProgressViewModel</c>) and no sender anywhere, so the run page's
/// progress bar could never move and every test of the receiver still passed. A messenger message type that something registers for must be sent by
/// something in <c>app/src</c>. Narrow on purpose: a receiver is <c>.Register&lt;..., TMessage&gt;(</c>; a sender is <c>.Send(new TMessage</c> or
/// <c>.Send&lt;TMessage&gt;(</c>. DECISION: a message sent only by passing an existing variable (<c>Send(message)</c>) is not seen as a sender; none exists
/// in app/src today (MEASURED 2026-10-31: every Send is <c>Send(new ...)</c>), and the day one does, the guard says so and the fix is to send with <c>new</c>.
/// </summary>
public class MessageHasSenderGuardTests
{
    // Register<TRecipient, TMessage>(  or  Register<TMessage>(  : the message is the last type argument.
    private static readonly Regex ReceiverPattern = new(@"\.Register<(?:[^<>,()]+,\s*)?([\w.]+)>\s*\(", RegexOptions.Compiled);

    // Send(new Ns.TMessage   or   Send<Ns.TMessage>(
    private static readonly Regex SenderPattern = new(@"\.Send(?:<([\w.]+)>\s*\(|\(\s*new\s+([\w.]+))", RegexOptions.Compiled);

    /// <summary>Message type names (last segment) with a receiver and no sender across <paramref name="sources"/>, plus the type names seen received and sent.</summary>
    internal static (IReadOnlyList<string> Orphans, IReadOnlySet<string> Received, IReadOnlySet<string> Sent) Scan(IEnumerable<string> sources)
    {
        var received = new HashSet<string>(StringComparer.Ordinal);
        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in sources)
        {
            foreach (Match m in ReceiverPattern.Matches(text))
            {
                received.Add(Last(m.Groups[1].Value));
            }

            foreach (Match m in SenderPattern.Matches(text))
            {
                sent.Add(Last(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            }
        }

        return (received.Where(t => !sent.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList(), received, sent);
    }

    private static string Last(string name) => name[(name.LastIndexOf('.') + 1)..];

    private static IEnumerable<string> RealSources() => RepoPaths.AllCSharpFiles.Select(File.ReadAllText);

    [Fact]
    public void Every_message_type_something_registers_for_is_sent_by_something_in_app_src()
    {
        var (orphans, receivers, senders) = Scan(RealSources());

        // Vacuity: a scan that sees no receivers or no senders reports a clean tree for the wrong reason. Assert two known message types by NAME (a count floor drifts with the code):
        // both have a Register<...> site and a Send(new ...) site in app/src.
        foreach (var known in new[] { "RunProgressChangedMessage", "RunPhaseChangedMessage" })
        {
            receivers.ShouldContain(known, "the scan did not see this message's Register<...> site; the pattern or the file list is broken");
            senders.ShouldContain(known, "the scan did not see this message's Send(new ...) site; the pattern or the file list is broken");
        }

        orphans.ShouldBeEmpty("a receiver with no sender is wired to nothing: " + string.Join(", ", orphans));
    }

    [Fact]
    public void A_planted_receiver_with_no_sender_is_reported_by_value()
    {
        const string receiver = "class A { void F(IMessenger m) { m.Register<A, GhostMessage>(this, static (r, x) => { }); } }";
        const string unrelatedSender = "class B { void F(IMessenger m) { m.Send(new OtherMessage(1)); } }";

        var (orphans, _, _) = Scan([receiver, unrelatedSender]);

        orphans.ShouldBe(["GhostMessage"]);
    }

    [Theory]
    [InlineData("m.Send(new GhostMessage(1));")]
    [InlineData("m.Send(new Messaging.GhostMessage(\"a\", 2));")]
    [InlineData("m.Send<GhostMessage>(x);")]
    [InlineData("m.Send(\n    new GhostMessage(1));")]
    public void A_sender_in_any_of_the_accepted_shapes_clears_the_receiver(string sendCall)
    {
        const string receiver = "class A { void F(IMessenger m) { m.Register<GhostMessage>(this, (r, x) => { }); } }";

        var (orphans, receivers, senders) = Scan([receiver, "class B { void F(IMessenger m) { " + sendCall + " } }"]);

        orphans.ShouldBeEmpty();
        receivers.ShouldBe(["GhostMessage"]);
        senders.ShouldBe(["GhostMessage"]);
    }
}
