using System.Diagnostics;
using OpenTelemetry;
using Platform.Service;

namespace Platform.Service.Tests;

/// <summary>
/// RingBufferActivityExporter(start-lite OTEL_MODE=console)契約:
/// 200 span 環形緩衝、最新優先、attribute 白名單瘦身、併發安全。
/// 04-acceptance-test B-O-01~03、05(B-O-04 需 WebApplicationFactory,屬 Web.Tests)。
/// </summary>
public sealed class RingBufferActivityExporterTests
{
    private static Activity MakeActivity(string name, params (string Key, object? Value)[] tags)
    {
        var activity = new Activity(name);
        foreach (var (key, value) in tags)
        {
            activity.SetTag(key, value);
        }

        return activity;
    }

    private static void Export(RingBufferActivityExporter exporter, params Activity[] activities)
        => exporter.Export(new Batch<Activity>(activities, activities.Length));

    // B-O-01:on/off-point。200:全在;201:最早那筆被擠掉、count 恆 200。
    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void Export_CapacityBoundary_EvictsOldest(int count)
    {
        var exporter = new RingBufferActivityExporter();
        var activities = Enumerable.Range(0, count).Select(i => MakeActivity($"span-{i}")).ToArray();
        Export(exporter, activities);

        var spans = exporter.GetRecentSpans();
        var names = spans.Select(s => s.Name).ToHashSet();

        Assert.Equal(Math.Min(count, 200), spans.Count);
        if (count == 201)
        {
            Assert.DoesNotContain("span-0", names); // 最早被擠掉
            Assert.Contains("span-200", names);
        }
        else
        {
            Assert.Contains("span-0", names);
        }
    }

    // B-O-02:GetRecentSpans(3) 恰 3 筆、最新優先、是最後 3 筆。
    [Fact]
    public void GetRecentSpans_WithLimit_ReturnsNewestFirst()
    {
        var exporter = new RingBufferActivityExporter();
        Export(exporter, Enumerable.Range(0, 5).Select(i => MakeActivity($"span-{i}")).ToArray());

        var spans = exporter.GetRecentSpans(3);

        Assert.Equal(new[] { "span-4", "span-3", "span-2" }, spans.Select(s => s.Name).ToArray());
    }

    // B-O-03:attribute 白名單——保留 skill_name/model,丟棄白名單外的 key。
    [Fact]
    public void Export_ExtractsWhitelistedAttributesOnly()
    {
        var exporter = new RingBufferActivityExporter();
        Export(exporter, MakeActivity(
            "chat.service",
            ("skill_name", "rag-qa"),
            ("model", "mock-gpt"),
            ("secret_internal", "leak")));

        var span = exporter.GetRecentSpans().Single();

        Assert.Equal("rag-qa", span.Attributes["skill_name"]);
        Assert.Equal("mock-gpt", span.Attributes["model"]);
        Assert.False(span.Attributes.ContainsKey("secret_internal"));
    }

    // B-O-05:併發 Export 與 GetRecentSpans 交錯不 throw;count 恆 ≤ 200。
    [Fact]
    public async Task Concurrent_ExportAndRead_DoesNotThrow()
    {
        var exporter = new RingBufferActivityExporter();

        var tasks = Enumerable.Range(0, 16).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                Export(exporter, MakeActivity($"span-{t}-{i}"));
                Assert.True(exporter.GetRecentSpans().Count <= 200);
            }
        }));

        await Task.WhenAll(tasks);
        Assert.True(exporter.GetRecentSpans().Count <= 200);
    }
}
