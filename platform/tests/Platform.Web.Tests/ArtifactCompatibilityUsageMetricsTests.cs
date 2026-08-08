using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Platform.Web.Infrastructure;

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

    // 「只用有界維度」是 P3-R2 的硬要求(根 AGENTS.md 不變量),語意鏡像 workflow/app/usage_evidence.py
    // 的 _ALLOWED fail-fast:未知值寧可炸掉也不能靜默寫出高基數標籤把整份證據作廢。
    // 現有呼叫端全是字面常數,所以這條只在有人新增維度值時才會踩到 —— 這正是它存在的理由。
    [Theory]
    [InlineData("public_skills", false)]              // AUTHORITY["platform"] 內的合法 surface
    [InlineData("public_business_workflows", false)]
    [InlineData("workflow_unified_invoke", true)]     // workflow 的 surface,platform 不是它的權威
    [InlineData("public_skills_v2", true)]
    public async Task UsageDimensions_AreBounded(string surface, bool rejected)
    {
        using var listener = Listen(out var measurements);
        var context = new DefaultHttpContext();
        Task<JsonElement> Action() => Task.FromResult(
            JsonDocument.Parse("""{"valid":true}""").RootElement.Clone());

        if (rejected)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => ArtifactCompatibilityUsageMetrics.TrackValidationAsync(context, surface, Action));
            Assert.Empty(measurements);
        }
        else
        {
            await ArtifactCompatibilityUsageMetrics.TrackValidationAsync(context, surface, Action);
            Assert.Equal(surface, Assert.Single(measurements)["surface"]);
        }
    }

    [Theory]
    [InlineData("public_skills", "validate", false)]
    [InlineData("public_skills", "invoke", false)]
    [InlineData("public_business_workflows", "validate", false)]
    [InlineData("public_business_workflows", "invoke", true)]
    public void UsageSurfaceOperationPairs_AreAuthoritative(
        string surface, string operation, bool rejected)
    {
        if (rejected)
        {
            Assert.Throws<ArgumentException>(
                () => ArtifactCompatibilityUsageMetrics.RequireAuthoritative(surface, operation));
        }
        else
        {
            ArtifactCompatibilityUsageMetrics.RequireAuthoritative(surface, operation);
        }
    }

    // 觀測絕不能改變 API 回應:錯誤路徑上白名單 fail-fast 會**取代**呼叫端正在往上丟的例外
    // (下游 4xx 會變成 500)。此時只能吞掉證據並記錄,原例外照常傳播(成功路徑仍 fail-fast,見上)。
    [Fact]
    public async Task UsageDimensions_ErrorPath_DoesNotReplaceOriginalException()
    {
        using var listener = Listen(out var measurements);
        var context = new DefaultHttpContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArtifactCompatibilityUsageMetrics.TrackValidationAsync(
                context,
                "workflow_unified_invoke",
                () => throw new InvalidOperationException("downstream")));

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
