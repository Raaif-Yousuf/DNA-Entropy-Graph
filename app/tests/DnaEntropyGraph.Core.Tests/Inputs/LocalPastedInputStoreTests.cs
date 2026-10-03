using System.Text;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>Issue #63: a pasted sequence is written under the app data folder, never next to anything of the user's (Hard Rule 14).</summary>
public sealed class LocalPastedInputStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-paste-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    [Fact]
    public void The_text_is_saved_under_the_pasted_folder_of_the_app_data_root()
    {
        var path = new LocalPastedInputStore(_root).Save("ACGTACGTAC");

        Path.GetDirectoryName(path).ShouldBe(Path.Combine(_root, "pasted"));
        File.ReadAllText(path).ShouldBe("ACGTACGTAC");
    }

    [Fact]
    public void The_file_is_utf8_without_a_byte_order_mark_and_uses_LF()
    {
        var path = new LocalPastedInputStore(_root).Save(">r\r\nACGT\r\nAC");

        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(bytes).ShouldBe(">r\nACGT\nAC");
    }

    [Fact]
    public void Two_pastes_never_share_a_file()
    {
        var store = new LocalPastedInputStore(_root);

        store.Save("ACGTACGTAC").ShouldNotBe(store.Save("ACGTACGTAC"));
    }

    [Fact]
    public void The_saved_file_is_one_the_validator_accepts_with_the_content_sniffed()
    {
        var path = new LocalPastedInputStore(_root).Save("ACGTACGTACGTACGT");

        var result = InputFileValidator.Validate(path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false);

        result.IsValid.ShouldBeTrue();
        result.TotalLength.ShouldBe(16);
    }
}
