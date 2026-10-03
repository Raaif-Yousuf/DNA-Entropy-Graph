using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #54: the link action of <see cref="SetupErrorCodes.WorkerDefaultAccountMissing"/>.</summary>
public class ServiceAccountLinksTests
{
    [Fact]
    public void The_link_opens_the_service_accounts_page_of_that_project()
    {
        ServiceAccountLinks.ForProject("my-lab").ShouldBe("https://console.cloud.google.com/iam-admin/serviceaccounts?project=my-lab");
    }

    [Fact]
    public void The_project_id_is_escaped_in_the_link()
    {
        ServiceAccountLinks.ForProject("a b&c").ShouldBe("https://console.cloud.google.com/iam-admin/serviceaccounts?project=a%20b%26c");
    }
}
