using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #1086, wired-to-nothing: <c>RunProgressChangedMessage</c> had a receiver (<c>RunProgressViewModel</c>) and no sender anywhere, so the run page's
/// progress bar could never move and every test of the receiver still passed. A messenger message type that something registers for must be sent by
/// something in <c>app/src</c>. Narrow on purpose: a receiver is <c>.Register&lt;..., TMessage&gt;(</c> or an <c>IRecipient&lt;TMessage&gt;</c> implementation, in code only (comments are stripped first); a sender is <c>.Send(new TMessage</c> or
/// <c>.Send&lt;TMessage&gt;(</c>. DECISION: a message sent only by passing an existing variable (<c>Send(message)</c>) is not seen as a sender; none exists
/// in app/src today (MEASURED 2026-10-31: every Send is <c>Send(new ...)</c>), and the day one does, the guard says so and the fix is to send with <c>new</c>.
/// </summary>
public class MessageHasSenderGuardTests
{
    // Register<TRecipient, TMessage>(  or  Register<TMessage>(  : the message is the last type argument.
    private static readonly Regex ReceiverPattern = new(@"\.Register<(?:[^<>,()]+,\s*)?([\w.]+)>\s*\(", RegexOptions.Compiled);

    // Send(new Ns.TMessage   or   Send<Ns.TMessage>(
    private static readonly Regex SenderPattern = new(@"\.Send(?:<([\w.]+)>\s*\(|\(\s*new\s+([\w.]+))", RegexOptions.Compiled);

    // class X : IRecipient<TMessage>  : the message is a receiver by interface (CommunityToolkit.Mvvm's other way to receive).
    private static readonly Regex RecipientInterfacePattern = new(@"\bIRecipient<([\w.]+)>", RegexOptions.Compiled);

    // A string or char literal (kept as is, so a "//" inside one is not taken for a comment) or a comment (dropped). Raw string literals ("""...""") are not handled; none holds a Send/Register call.
    private static readonly Regex CommentOrLiteralPattern = new(
        @"(?<lit>@""(?:""""|[^""])*""|""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])+')|//[^\r\n]*|/\*[\s\S]*?\*/",
        RegexOptions.Compiled);

    /// <summary>The source with every <c>//</c>, <c>///</c> and <c>/* */</c> comment removed, so a commented-out call is not mistaken for a real one.</summary>
    internal static string StripComments(string text) =>
        CommentOrLiteralPattern.Replace(text, m => m.Groups["lit"].Success ? m.Value : " ");

    /// <summary>Message type names (last segment) with a receiver and no sender across <paramref name="sources"/>, plus the type names seen received and sent.</summary>
    internal static (IReadOnlyList<string> Orphans, IReadOnlySet<string> Received, IReadOnlySet<string> Sent) Scan(IEnumerable<string> sources)
    {
        var received = new HashSet<string>(StringComparer.Ordinal);
        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in sources)
        {
            var text = StripComments(raw);
            foreach (Match m in ReceiverPattern.Matches(text))
            {
                received.Add(Last(m.Groups[1].Value));
            }

            foreach (Match m in RecipientInterfacePattern.Matches(text))
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
    [InlineData("// m.Send(new GhostMessage(1));")]
    [InlineData("/// <c>m.Send(new GhostMessage(1))</c> is the sender")]
    [InlineData("/* m.Send(new GhostMessage(1)); */")]
    [InlineData("/* line one\n   m.Send(new GhostMessage(1)); */")]
    public void A_sender_that_is_only_in_a_comment_does_not_clear_the_receiver(string commentedSend)
    {
        const string receiver = "class A { void F(IMessenger m) { m.Register<GhostMessage>(this, (r, x) => { }); } }";

        var (orphans, _, senders) = Scan([receiver, "class B { void F(IMessenger m) {\n" + commentedSend + "\n} }"]);

        orphans.ShouldBe(["GhostMessage"]);
        senders.ShouldBeEmpty();
    }

    [Fact]
    public void A_comment_marker_inside_a_string_does_not_hide_the_real_code_after_it()
    {
        const string receiver = "class A { void F(IMessenger m) { m.Register<GhostMessage>(this, (r, x) => { }); } }";
        const string sender = "class B { void F(IMessenger m) { var u = \"http://example\"; m.Send(new GhostMessage(1)); } }";

        var (orphans, _, _) = Scan([receiver, sender]);

        orphans.ShouldBeEmpty();
    }

    [Fact]
    public void A_receiver_that_is_only_in_a_comment_is_not_a_receiver()
    {
        var (_, receivers, _) = Scan(["class A { // m.Register<GhostMessage>(this, (r, x) => { });\n}"]);

        receivers.ShouldBeEmpty();
    }

    [Fact]
    public void An_IRecipient_implementation_is_a_receiver_and_is_orphaned_without_a_sender()
    {
        const string recipient = "class A : ObservableRecipient, IRecipient<GhostMessage> { public void Receive(GhostMessage message) { } }";

        var (orphans, receivers, _) = Scan([recipient]);

        receivers.ShouldBe(["GhostMessage"]);
        orphans.ShouldBe(["GhostMessage"]);
    }

    [Fact]
    public void An_IRecipient_implementation_with_a_sender_is_clean()
    {
        const string recipient = "class A : IRecipient<Ns.GhostMessage> { public void Receive(Ns.GhostMessage message) { } }";
        const string sender = "class B { void F(IMessenger m) { m.Send(new GhostMessage(1)); } }";

        var (orphans, _, _) = Scan([recipient, sender]);

        orphans.ShouldBeEmpty();
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
