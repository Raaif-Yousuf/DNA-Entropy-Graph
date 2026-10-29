using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #144: paths under the portable folder are stored relative to it, so moving the stick to another drive letter still finds the results.</summary>
public sealed class DataPathMapperTests
{
    [Fact]
    public void A_path_under_the_portable_base_round_trips_to_a_new_drive_letter()
    {
        var before = new DataPathMapper(@"D:\stick");
        var after = new DataPathMapper(@"E:\lab\stick");

        var stored = before.ToStored(@"D:\stick\outputs\run one");
        stored.ShouldBe(@"portable:outputs\run one");
        after.FromStored(stored).ShouldBe(@"E:\lab\stick\outputs\run one");
    }

    [Fact]
    public void A_path_outside_the_portable_base_is_stored_as_it_is_and_the_user_choice_is_kept()
    {
        var mapper = new DataPathMapper(@"D:\stick");

        mapper.ToStored(@"C:\Users\me\Documents\out").ShouldBe(@"C:\Users\me\Documents\out");
        mapper.FromStored(@"C:\Users\me\Documents\out").ShouldBe(@"C:\Users\me\Documents\out");
        mapper.ToStored(@"D:\stick-other\x").ShouldBe(@"D:\stick-other\x", "a sibling that only shares the name prefix is not inside");
    }

    [Fact]
    public void Without_a_portable_base_nothing_is_changed_and_null_stays_null()
    {
        var mapper = new DataPathMapper(null);

        mapper.ToStored(@"D:\stick\outputs\x").ShouldBe(@"D:\stick\outputs\x");
        mapper.FromStored(@"portable:outputs\x").ShouldBe(@"portable:outputs\x");
        mapper.ToStored(null).ShouldBeNull();
        new DataPathMapper(@"D:\stick").FromStored(null).ShouldBeNull();
    }

    [Fact]
    public void The_portable_folder_itself_and_a_case_difference_are_handled()
    {
        var mapper = new DataPathMapper(@"D:\stick");

        mapper.ToStored(@"D:\stick").ShouldBe("portable:");
        mapper.ToStored(@"d:\STICK\outputs").ShouldBe(@"portable:outputs");
        mapper.FromStored("portable:").ShouldBe(@"D:\stick");
    }

    [Theory]
    [InlineData(@"portable:..\..\x")]
    [InlineData(@"portable:outputs\..\..\x")]
    [InlineData(@"portable:C:\Windows\x")]
    [InlineData(@"portable:\\server\share\x")]
    [InlineData(@"portable:\x")]
    public void A_stored_path_that_would_expand_outside_the_portable_folder_is_left_unexpanded(string stored)
    {
        // A corrupt or hand-edited history row must not make the app read or write outside the stick (Hard Rule 14).
        new DataPathMapper(@"D:\stick").FromStored(stored).ShouldBe(stored);
    }
}
