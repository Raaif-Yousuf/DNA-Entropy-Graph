using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #53: the pure rules for the results bucket's name, labels and config file.</summary>
public class ResultsBucketTests
{
    [Fact]
    public void A_name_is_deg_then_the_project_number_then_six_lowercase_base32_characters()
    {
        var names = Enumerable.Range(0, 50).Select(i => ResultsBucket.NewName("123456789012", new Random(i))).ToList();

        names.ShouldAllBe(n => Regex.IsMatch(n, "^deg-123456789012-[a-z2-7]{6}$"));
        names.Distinct().Count().ShouldBeGreaterThan(40);
    }

    [Theory]
    [InlineData("my-lab")]
    [InlineData("")]
    [InlineData("12 3")]
    public void A_project_id_or_anything_not_digits_is_refused_so_the_name_cannot_carry_it(string notANumber)
    {
        Should.Throw<ArgumentException>(() => ResultsBucket.NewName(notANumber, new Random(1)));
    }

    [Fact]
    public void Labels_are_the_standard_set_without_the_per_run_ones_and_the_version_is_label_safe()
    {
        var labels = ResultsBucket.Labels("inst-1", "0.1.0+Build.5");

        labels.ShouldBe(new Dictionary<string, string>
        {
            ["app"] = "dna-entropy-graph",
            ["installation-id"] = "inst-1",
            ["app-version"] = "0-1-0-build-5",
            ["lifecycle"] = "results",
        });
    }

    [Fact]
    public void Labels_without_an_installation_id_are_refused()
    {
        Should.Throw<ArgumentException>(() => ResultsBucket.Labels(" ", "0.1.0"));
    }

    [Fact]
    public void The_config_file_is_LF_json_with_the_retention_and_no_carriage_returns()
    {
        var json = ResultsBucket.ConfigJson("inst-1", "0.1.0", 30, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

        json.ShouldNotContain("\r");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        document.RootElement.GetProperty("resultsRetentionDays").GetInt32().ShouldBe(30);
        document.RootElement.GetProperty("cacheRetentionDays").GetInt32().ShouldBe(365);
        document.RootElement.GetProperty("createdUtc").GetString().ShouldBe("2026-10-03T12:00:00Z");
    }

    [Fact]
    public void The_config_file_escapes_quotes_and_backslashes_in_the_ids()
    {
        var json = ResultsBucket.ConfigJson("in\"st\\1", "0.1.0\"x", 30, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

        using var document = System.Text.Json.JsonDocument.Parse(json);
        document.RootElement.GetProperty("installationId").GetString().ShouldBe("in\"st\\1");
        document.RootElement.GetProperty("appVersion").GetString().ShouldBe("0.1.0\"x");
        json.ShouldNotContain("\r");
        json.ShouldEndWith("\n");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(90)]
    [InlineData(365)]
    [InlineData(ResultsBucket.MaxRetentionDays)]
    public void A_sane_retention_is_accepted(int days)
    {
        Should.NotThrow(() => ResultsBucket.ValidateRetentionDays(days));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ResultsBucket.MaxRetentionDays + 1)]
    public void A_retention_that_would_delete_results_at_once_or_that_Google_rejects_is_refused(int days)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ResultsBucket.ValidateRetentionDays(days));
    }

    [Fact]
    public void The_run_option_default_is_the_bucket_default_so_the_two_cannot_drift()
    {
        new DnaEntropyGraph.Core.RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }.CloudResultsRetentionDays.ShouldBe(ResultsBucket.DefaultRetentionDays);
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("a.b")]
    [InlineData("")]
    public void Labels_refuse_an_installation_id_that_is_not_a_label_value(string id)
    {
        Should.Throw<ArgumentException>(() => ResultsBucket.Labels(id, "0.1.0"));
    }

    [Fact]
    public void Labels_accept_the_generated_installation_id_shape()
    {
        Should.NotThrow(() => ResultsBucket.Labels(Guid.NewGuid().ToString("n"), "0.1.0"));
    }
}