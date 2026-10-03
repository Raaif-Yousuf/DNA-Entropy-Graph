using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #51: the billing deep link and the copyable request text.</summary>
public class BillingLinksTests
{
    [Fact]
    public void The_deep_link_opens_the_billing_page_for_that_project()
    {
        BillingLinks.ForProject("my-lab").ShouldBe("https://console.cloud.google.com/billing/linkedaccount?project=my-lab");
    }

    [Fact]
    public void The_project_id_is_escaped_in_the_link()
    {
        BillingLinks.ForProject("a b&c").ShouldBe("https://console.cloud.google.com/billing/linkedaccount?project=a%20b%26c");
    }

    [Fact]
    public void Creating_a_billing_account_has_its_own_link()
    {
        BillingLinks.CreateAccount.ShouldBe("https://console.cloud.google.com/billing/create");
    }

    [Fact]
    public void The_request_text_fills_in_the_project_and_the_account()
    {
        var text = BillingRequestText.Fill("Please link {project} to {account}. {project} again.", "my-lab", "billingAccounts/AAA");

        text.ShouldBe("Please link my-lab to billingAccounts/AAA. my-lab again.");
    }
}
