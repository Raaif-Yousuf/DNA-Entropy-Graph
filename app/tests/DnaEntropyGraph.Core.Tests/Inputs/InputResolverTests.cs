using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #211: port of the prototype's double-click wizard resolution rule
/// (<c>DNA-Entropy-Genbank/packaging/launcher.py::_resolve_input</c>, read directly from
/// the sibling prototype checkout since the file was deleted from this repo's worker under
/// issue #291 - see this lane's report for the exact source read). Every case here mirrors
/// one branch of that function exactly.
/// </summary>
public sealed class InputResolverTests
{
    [Fact]
    public void An_existing_file_path_resolves_to_ExistingFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var result = InputResolver.Resolve(path);
            result.Kind.ShouldBe(InputResolutionKind.ExistingFile);
            result.FilePath.ShouldBe(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_double_quoted_existing_path_pasted_from_Explorers_Copy_as_path_is_unwrapped()
    {
        var path = Path.GetTempFileName();
        try
        {
            var result = InputResolver.Resolve($"\"{path}\"");
            result.Kind.ShouldBe(InputResolutionKind.ExistingFile);
            result.FilePath.ShouldBe(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_single_quoted_path_is_also_unwrapped()
    {
        var path = Path.GetTempFileName();
        try
        {
            InputResolver.Resolve($"'{path}'").Kind.ShouldBe(InputResolutionKind.ExistingFile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_before_and_after_quote_stripping()
    {
        var path = Path.GetTempFileName();
        try
        {
            InputResolver.Resolve($"   \"{path}\"   ").Kind.ShouldBe(InputResolutionKind.ExistingFile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(@"C:\some\made\up\path.fasta")]
    [InlineData("relative/looking/path.gb")]
    [InlineData(@"C:no_such_drive_relative_path.fa")]
    public void Text_that_looks_like_a_path_but_does_not_exist_is_PathNotFound_not_a_sequence_attempt(string attempted)
    {
        var result = InputResolver.Resolve(attempted);
        result.Kind.ShouldBe(InputResolutionKind.PathNotFound);
        result.AttemptedPath.ShouldBe(attempted);
    }

    [Fact]
    public void Nothing_entered_is_Empty()
    {
        InputResolver.Resolve("").Kind.ShouldBe(InputResolutionKind.Empty);
        InputResolver.Resolve("   ").Kind.ShouldBe(InputResolutionKind.Empty);
        InputResolver.Resolve(null).Kind.ShouldBe(InputResolutionKind.Empty);
    }

    [Fact]
    public void A_pasted_DNA_sequence_that_is_not_a_path_shape_is_PastedSequence()
    {
        var result = InputResolver.Resolve("ACGTACGTACGTACGT");
        result.Kind.ShouldBe(InputResolutionKind.PastedSequence);
        result.SequenceText.ShouldBe("ACGTACGTACGTACGT");
    }

    [Fact]
    public void A_pasted_sequence_with_surrounding_whitespace_and_quotes_is_still_recognised()
    {
        // A biologist pasting a sequence copied from a quoted context (e.g. a spreadsheet
        // cell) should not be misdiagnosed as "file not found".
        var result = InputResolver.Resolve("  \"ACGTACGT\"  ");
        result.Kind.ShouldBe(InputResolutionKind.PastedSequence);
        result.SequenceText.ShouldBe("ACGTACGT");
    }

    [Fact]
    public void The_drop_zone_and_the_paste_box_behave_identically_for_the_same_existing_path()
    {
        // Issue #211's own "Why": both entry points must decide the same thing.
        var path = Path.GetTempFileName();
        try
        {
            var fromDrop = InputResolver.Resolve(path); // shell hands over a bare, already-resolved path
            var fromPaste = InputResolver.Resolve($"\"{path}\""); // user pastes "Copy as path" output
            fromDrop.Kind.ShouldBe(fromPaste.Kind);
            fromDrop.FilePath.ShouldBe(fromPaste.FilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
