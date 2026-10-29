using DnaEntropyGraph.Presentation.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #701: switching Windows high contrast while the app runs re-applies the window theme (the caption-button palette is decided at apply time).</summary>
public class HighContrastThemeReapplierTests
{
    private readonly IHighContrastSource _source = Substitute.For<IHighContrastSource>();
    private readonly IThemeApplier _applier = Substitute.For<IThemeApplier>();
    private readonly ManualDispatcher _dispatcher = new();

    private HighContrastThemeReapplier Started()
    {
        var reapplier = new HighContrastThemeReapplier(_source, _applier, _dispatcher);
        reapplier.Start();
        return reapplier;
    }

    [Fact]
    public void A_high_contrast_change_re_applies_the_current_theme_on_the_ui_thread()
    {
        _applier.CurrentTheme.Returns("Dark");
        Started();

        _source.Changed += Raise.Event();

        _applier.DidNotReceive().Apply(Arg.Any<string>());
        _dispatcher.RunAll();
        _applier.Received(1).Apply("Dark");
    }

    [Fact]
    public void Before_any_theme_was_applied_a_change_re_applies_the_system_theme()
    {
        _applier.CurrentTheme.Returns((string?)null);
        Started();

        _source.Changed += Raise.Event();
        _dispatcher.RunAll();

        _applier.Received(1).Apply("System");
    }

    [Fact]
    public void Nothing_is_applied_before_the_change_arrives()
    {
        Started();

        _dispatcher.RunAll();

        _applier.DidNotReceive().Apply(Arg.Any<string>());
    }

    [Fact]
    public void Starting_twice_does_not_apply_twice_per_change()
    {
        _applier.CurrentTheme.Returns("Light");
        var reapplier = Started();
        reapplier.Start();

        _source.Changed += Raise.Event();
        _dispatcher.RunAll();

        _applier.Received(1).Apply("Light");
    }
}
