using System.Text;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

public class OAuthClientLoaderTests
{
    private static string Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "deg-client-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void Reads_the_Google_downloaded_desktop_client_shape()
    {
        var dir = TempDir();
        var path = Write(dir, "c.json", "{\"installed\":{\"client_id\":\"id.apps.googleusercontent.com\",\"client_secret\":\"s\",\"redirect_uris\":[\"http://localhost\"]}}");

        var client = new OAuthClientLoader([path]).Load();

        client.ClientId.ShouldBe("id.apps.googleusercontent.com");
        client.ClientSecret.ShouldBe("s");
    }

    [Fact]
    public void The_first_existing_candidate_wins_and_a_missing_one_is_skipped()
    {
        var dir = TempDir();
        var good = Write(dir, "good.json", "{\"installed\":{\"client_id\":\"a\",\"client_secret\":\"b\"}}");

        new OAuthClientLoader([Path.Combine(dir, "nope.json"), good]).Load().ClientId.ShouldBe("a");
    }

    [Fact]
    public void No_candidate_is_OAUTH_CLIENT_MISSING()
    {
        var failure = Should.Throw<AccountAuthException>(() => new OAuthClientLoader([Path.Combine(TempDir(), "nope.json")]).Load());

        failure.Code.ShouldBe(AuthErrorCodes.OAuthClientMissing);
        Should.Throw<AccountAuthException>(() => new OAuthClientLoader([]).Load()).Code.ShouldBe(AuthErrorCodes.OAuthClientMissing);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"web\":{\"client_id\":\"a\",\"client_secret\":\"b\"}}")]
    [InlineData("{\"installed\":{\"client_id\":\"a\"}}")]
    [InlineData("{\"installed\":{\"client_id\":\"\",\"client_secret\":\"b\"}}")]
    [InlineData("[]")]
    [InlineData("{\"installed\":\"x\"}")]
    public void A_file_that_is_not_a_desktop_client_download_is_OAUTH_CLIENT_INVALID(string content)
    {
        var path = Write(TempDir(), "c.json", content);

        Should.Throw<AccountAuthException>(() => new OAuthClientLoader([path]).Load()).Code.ShouldBe(AuthErrorCodes.OAuthClientInvalid);
    }

    [Fact]
    public void The_default_candidates_include_the_dev_secrets_folder_and_the_app_data_folder()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo"));
        var baseDirectory = Path.Combine(root, "app", "src", "X", "bin", "Debug", "net10.0");
        var candidates = OAuthClientLoader.DefaultCandidates(Path.Combine(root, "data"), baseDirectory);

        candidates.ShouldContain(Path.Combine(root, "data", "oauth_client.local.json"));
        candidates.ShouldContain(Path.Combine(baseDirectory, "oauth_client.local.json"));
        candidates.ShouldContain(Path.Combine(root, "app", "secrets", "oauth_client.local.json"));
    }
}
