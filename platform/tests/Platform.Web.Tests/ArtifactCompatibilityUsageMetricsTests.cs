using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Platform.Web.Tests;

[Collection("EngineCalls")]
public sealed class ArtifactCompatibilityUsageMetricsTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ArtifactCompatibilityUsageMetricsTests(TestWebAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/skills/validate", "public_skills")]
    [InlineData("/api/business-workflows/validate", "public_business_workflows")]
    public async Task PublicValidation_RecordsExactlyOnceAtPlatform(string path, string surface)
    {
        using var listener = Listen(out var measurements);

        var response = await _factory.AdminClient().PostAsJsonAsync(
            path, new { definition = "name: quarterly-flow" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var measurement = Assert.Single(measurements, item => Equals(item["surface"], surface));
        Assert.Equal(1L, measurement["value"]);
        Assert.Equal("platform", measurement["service"]);
        Assert.Equal("validate", measurement["operation"]);
        Assert.Equal("business_workflow", measurement["resolved_artifact_type"]);
        Assert.Equal("success", measurement["outcome"]);
    }

    [Theory]
    [InlineData("/api/skills/validate", "public_skills", "unauthorized")]
    [InlineData("/api/skills/validate", "public_skills", "model_binding")]
    [InlineData("/api/skills/validate", "public_skills", "malformed")]
    [InlineData("/api/business-workflows/validate", "public_business_workflows", "unauthorized")]
    public async Task PublicValidation_PreControllerRejection_RecordsFallback(
        string path, string surface, string scenario)
    {
        using var listener = Listen(out var measurements);

        var response = await SendRejectedAsync(path, scenario);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        AssertFallback(Assert.Single(measurements), surface, "validate");
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("model_binding")]
    [InlineData("malformed")]
    public async Task PublicInvoke_PreControllerRejection_RecordsFallback(string scenario)
    {
        using var listener = Listen(out var measurements);

        var response = await SendRejectedAsync(
            "/api/skills/quarterly-qa/invoke", scenario);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        AssertFallback(Assert.Single(measurements), "public_skills", "invoke");
    }

    [Fact]
    public async Task PublicInvoke_ReachedAction_IsNotCountedAtPlatform()
    {
        using var listener = Listen(out var measurements);

        var response = await _factory.AdminClient().PostAsJsonAsync(
            "/api/skills/quarterly-qa/invoke", new { input = new { query = "2025Q3" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(measurements);
    }

    private async Task<HttpResponseMessage> SendRejectedAsync(string path, string scenario)
    {
        var client = scenario == "unauthorized" ? _factory.CreateClient() : _factory.AdminClient();
        HttpContent content = scenario switch
        {
            "malformed" => new StringContent("{", Encoding.UTF8, "application/json"),
            _ => JsonContent.Create(new { }),
        };
        return await client.PostAsync(path, content);
    }

    private static MeterListener Listen(out List<Dictionary<string, object?>> measurements)
    {
        measurements = [];
        var captured = measurements;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == "Platform.Web.ArtifactCompatibilityUsage"
                    && instrument.Name == "artifact_compatibility_usage_total")
                {
                    current.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var measurement = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            measurement["value"] = value;
            captured.Add(measurement);
        });
        listener.Start();
        return listener;
    }

    private static void AssertFallback(
        Dictionary<string, object?> measurement, string surface, string operation)
    {
        Assert.Equal(1L, measurement["value"]);
        Assert.Equal("platform", measurement["service"]);
        Assert.Equal(surface, measurement["surface"]);
        Assert.Equal(operation, measurement["operation"]);
        Assert.Equal("unknown", measurement["resolved_artifact_type"]);
        Assert.Equal("rejected", measurement["outcome"]);
    }
}
