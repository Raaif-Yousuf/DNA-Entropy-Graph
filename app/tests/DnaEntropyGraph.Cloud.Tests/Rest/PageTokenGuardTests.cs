using System.Text.Json;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>Issue #933: the one paging guard every Google list loop in <c>Rest/</c> uses.</summary>
public class PageTokenGuardTests
{
    [Fact]
    public void A_missing_or_empty_next_token_ends_the_walk()
    {
        var guard = new PageTokenGuard();

        guard.Advance((string?)null).ShouldBeNull();
        guard.Advance(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void Distinct_tokens_are_passed_through_in_order()
    {
        var guard = new PageTokenGuard();

        guard.Advance("a").ShouldBe("a");
        guard.Advance("b").ShouldBe("b");
        guard.Advance("c").ShouldBe("c");
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a", "b", "a")]
    [InlineData("a", "b", "c", "b")]
    public void Any_token_seen_before_fails_as_a_stuck_listing_of_kind_Network(params string[] walk)
    {
        var guard = new PageTokenGuard();
        var thrown = default(CloudOperationException);
        foreach (var token in walk)
        {
            try
            {
                guard.Advance(token);
            }
            catch (CloudOperationException ex)
            {
                thrown = ex;
                break;
            }
        }

        thrown.ShouldNotBeNull();
        thrown.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        thrown.Kind.ShouldBe(CloudErrorKind.Network);
        // The pipeline decides "retry or not" from Classify(error) as well as from the kind: a message with a classifier keyword ("billing account", "quota", "permission")
        // would read as a permanent error and silently switch the replay off. The wording must classify as Other, the class nothing else claims.
        CloudErrorClassifier.Classify(thrown.Error).ShouldBe(CloudErrorKind.Other);
    }

    [Fact]
    public void A_json_page_is_read_by_its_nextPageToken_member_and_guarded_the_same_way()
    {
        var guard = new PageTokenGuard();
        using var first = JsonDocument.Parse("{\"nextPageToken\":\"a\"}");
        using var last = JsonDocument.Parse("{\"quotaPreferences\":[]}");
        using var number = JsonDocument.Parse("{\"nextPageToken\":5}");

        guard.Advance(first.RootElement).ShouldBe("a");
        Should.Throw<CloudOperationException>(() => guard.Advance(first.RootElement)).Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        new PageTokenGuard().Advance(last.RootElement).ShouldBeNull();
        new PageTokenGuard().Advance(number.RootElement).ShouldBeNull("only a string is a token");
    }
}
