using DnaEntropyGraph.App.Services;
using Microsoft.UI.Xaml;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// Issue #62's "ElementTheme applied from settings at startup (default
/// System)". <see cref="ThemeApplier.Resolve"/> is pulled out as a pure
/// function specifically so this mapping is testable with no
/// FrameworkElement and no UI thread - the branch that decides Light vs
/// Dark vs Default cannot live in MainWindow.xaml.cs (Hard Rule 8: no
/// branching in code-behind), so it has to live somewhere, and a plain,
/// directly-testable static method is that somewhere.
/// </summary>
public class ThemeApplierTests
{
    [Theory]
    [InlineData("Light", ElementTheme.Light)]
    [InlineData("Dark", ElementTheme.Dark)]
    [InlineData("System", ElementTheme.Default)]
    [InlineData(null, ElementTheme.Default)]
    [InlineData("", ElementTheme.Default)]
    [InlineData("garbage", ElementTheme.Default)]
    public void Resolve_maps_the_saved_setting_to_the_right_ElementTheme(string? saved, ElementTheme expected)
    {
        ThemeApplier.Resolve(saved).ShouldBe(expected);
    }
}
