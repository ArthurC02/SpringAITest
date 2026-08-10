using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Data.InMemory;
using Backend.Api.Orchestrators;
using Backend.Api.Triggers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// HTTP-level O5 acceptance (04-operations-trigger-plan.md §6): flag/capability gating, the pinned
/// target preflight, input-mapping sanitisation bounds, the DTO allowlist, If-Match cancel, and the
/// occurrence keyset page. Fire-time behaviour lives in <see cref="TriggerDispatchTests"/>; Dapper
/// parity lives in <see cref="TriggerRepositoryPostgresTests"/>.
/// </summary>
public sealed class TriggerApiTests : IClassFixture<TriggerApiTests.Factory>
{
    internal static readonly Guid PublishedId = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    internal static readonly Guid DisabledId = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    internal static readonly Guid UnpublishedId = Guid.Parse("a0000000-0000-4000-8000-000000000003");
    internal const int PublishedRevision = 2;

    // W2-03 之後 fire_at 有 90 天上限,所以這裡不能再用固定的遠期常數(它會隨時間走出窗外,
    // 讓每個非 fire_at 主題的測試都變成在測那道上限)。取窗內的相對時刻。
    private static readonly DateTime FireAt = DateTime.UtcNow.AddDays(30);

    private readonly Factory _factory;

    public TriggerApiTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Routes_FlagOff_Return404IndistinguishableFromUnknownRoute()
    {
        // The base TestWebAppFactory never sets AGENT_TRIGGERS_ENABLED (defaults false).
        using var off = new TestWebAppFactory();
        using var client = Internal(off, "demo-a");

        var gated = await client.GetAsync("/api/admin/triggers");
        var unknown = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        var gatedBody = await gated.ReadJsonAsync();
        var unknownBody = await unknown.ReadJsonAsync();
        gatedBody.AssertApiError(404, "not_found");
        unknownBody.AssertApiError(404, "not_found");
        Assert.Equal(gatedBody["message"]!.GetValue<string>(), unknownBody["message"]!.GetValue<string>());
    }

    // The other half of the gate decision table: a write route is hidden too, and nothing reaches
    // the repository -- an off flag must not merely hide the read surface.
    [Fact]
    public async Task Create_FlagOff_Returns404AndWritesNothing()
    {
        using var off = new TestWebAppFactory();
        using var client = Internal(off, "demo-a");

        var response = await client.PostAsJsonAsync("/api/admin/triggers", Body());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        (await response.ReadJsonAsync()).AssertApiError(404, "not_found");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("workflow.manage.all")]
    [InlineData("WORKFLOW.MANAGE")]
    public async Task Create_WithoutExactWorkflowManage_IsForbidden(string? capability)
    {
        using var client = capability is null
            ? Internal(_factory, "demo-a")
            : Internal(_factory, "demo-a").WithCapabilities(capability);

        var response = await client.PostAsJsonAsync("/api/admin/triggers", Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_MissingTenantHeader_IsBadRequest()
    {
        using var client = _factory.CreateInternalClient().WithUser("op-a").WithRole("ADMIN")
            .WithCapabilities("workflow.manage");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/triggers")).StatusCode);
    }

    [Fact]
    public async Task Create_PinsPublishedTarget_AndReturnsAllowlistedFieldsWithETag()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);

        var response = await client.PostAsJsonAsync("/api/admin/triggers", Body(name: "nightly"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.ToString());
        var body = (await response.ReadJsonAsync()).AsObject();
        Assert.Equal(
            new[]
            {
                "created_at", "created_by", "description", "fire_at", "id", "input_mapping",
                "misfire_window_seconds", "name", "orchestrator_id", "orchestrator_revision",
                "status", "updated_at", "version",
            },
            body.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("scheduled", body["status"]!.GetValue<string>());
        Assert.Equal(1, body["version"]!.GetValue<long>());
        Assert.Equal("op-a", body["created_by"]!.GetValue<string>());
        Assert.Equal(PublishedRevision, body["orchestrator_revision"]!.GetValue<int>());
        // The immutable grant snapshot is stored but never projected: no role/groups/capabilities.
        Assert.DoesNotContain("ADMIN", body.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("workflow.manage", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RejectsUnknownDisabledUnpublishedOrMisPinnedTarget()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/admin/triggers", Body("a", orchestratorId: Guid.NewGuid()))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/admin/triggers", Body("b", orchestratorId: DisabledId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/admin/triggers", Body("c", orchestratorId: UnpublishedId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/admin/triggers", Body("d", revision: PublishedRevision - 1))).StatusCode);
    }

    // fire_at must be an absolute UTC instant: an offset-less or local-offset literal is rejected
    // rather than silently reinterpreted (timezone handling is a client input concern).
    [Theory]
    [InlineData("2030-01-01T00:00:00")]
    [InlineData("2030-01-01T00:00:00+08:00")]
    [InlineData("not-a-date")]
    public async Task Create_RejectsNonUtcFireAt(string fireAt)
    {
        using var client = Client(UniqueTenant());

        var response = await client.PostAsJsonAsync("/api/admin/triggers", new
        {
            name = "tz",
            orchestrator_id = PublishedId,
            orchestrator_revision = PublishedRevision,
            fire_at = fireAt,
            input_mapping = new { message = "go" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // On-point/off-point for every documented input-mapping bound plus the shape allowlist: nested
    // values, non-string values and a missing/blank message are all rejected at the trust boundary.
    public static TheoryData<object?, HttpStatusCode> InputMappings => new()
    {
        { new { message = "go" }, HttpStatusCode.Created },
        { new { message = new string('m', TriggerInputMapping.MaxValueLength) }, HttpStatusCode.Created },
        { new { message = new string('m', TriggerInputMapping.MaxValueLength + 1) }, HttpStatusCode.BadRequest },
        { new { message = "" }, HttpStatusCode.BadRequest },
        { new { message = "   " }, HttpStatusCode.BadRequest },
        { new { note = "no message" }, HttpStatusCode.BadRequest },
        { new { message = 5 }, HttpStatusCode.BadRequest },
        { new { message = new { nested = "x" } }, HttpStatusCode.BadRequest },
        { new { message = "go", @internal = new[] { "x" } }, HttpStatusCode.BadRequest },
        { null, HttpStatusCode.BadRequest },
    };

    [Theory]
    [MemberData(nameof(InputMappings))]
    public async Task Create_ValidatesInputMappingShapeAndBounds(object? mapping, HttpStatusCode expected)
    {
        using var client = Client(UniqueTenant());

        var response = await client.PostAsJsonAsync("/api/admin/triggers", new
        {
            name = "mapping-" + Guid.NewGuid().ToString("N"),
            orchestrator_id = PublishedId,
            orchestrator_revision = PublishedRevision,
            fire_at = FireAt,
            input_mapping = mapping,
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsKeyCountAboveBound_AndAcceptsExactlyTheBound()
    {
        using var client = Client(UniqueTenant());

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            "/api/admin/triggers", Body("on-point", mapping: Mapping(TriggerInputMapping.MaxEntries)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            "/api/admin/triggers", Body("off-point", mapping: Mapping(TriggerInputMapping.MaxEntries + 1)))).StatusCode);
    }

    // W2-03:fire_at 距現在不得超過 90 天。上限的意義是安全(不可變 principal 快照 = 引信長度),
    // 所以 on-point/off-point 兩側都要釘住:剛好在窗內必須建得起來,只超過一點點必須 400。
    // 一小時的緩衝是為了吸收「測試組出請求」到「伺服器讀時鐘」之間的時間差,不是在測邊界寬容度。
    [Fact]
    public async Task Create_FireAtAtHorizon_IsAccepted_JustBeyondHorizon_IsRejected()
    {
        using var client = Client(UniqueTenant());

        var onPoint = await client.PostAsJsonAsync(
            "/api/admin/triggers", Body("horizon-on", fireAt: DateTime.UtcNow.AddDays(90).AddHours(-1)));
        var offPoint = await client.PostAsJsonAsync(
            "/api/admin/triggers", Body("horizon-off", fireAt: DateTime.UtcNow.AddDays(90).AddHours(1)));

        Assert.Equal(HttpStatusCode.Created, onPoint.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, offPoint.StatusCode);
        (await offPoint.ReadJsonAsync()).AssertApiError(400, "validation_failed");
    }

    // 決策表的另一半:被拒的那一筆不得留下任何持久化痕跡(驗證在 repository 之前發生),
    // 且窗內的遠期值(89 天)不會因為「看起來很遠」就被擋掉。
    [Fact]
    public async Task Create_RejectedFireAt_WritesNothing_AndInWindowValueStillCreates()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/admin/triggers", Body("too-far", fireAt: DateTime.UtcNow.AddDays(400)))).StatusCode);
        Assert.Empty((await (await client.GetAsync("/api/admin/triggers")).ReadJsonAsync())["items"]!.AsArray());

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/admin/triggers", Body("in-window", fireAt: DateTime.UtcNow.AddDays(89)))).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86401)]
    public async Task Create_RejectsMisfireWindowOutOfRange(int seconds)
    {
        using var client = Client(UniqueTenant());

        var response = await client.PostAsJsonAsync(
            "/api/admin/triggers", Body("window", misfireWindowSeconds: seconds));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(86400)]
    public async Task Create_AcceptsMisfireWindowBounds(int seconds)
    {
        using var client = Client(UniqueTenant());

        var response = await client.PostAsJsonAsync(
            "/api/admin/triggers", Body("window-" + seconds, misfireWindowSeconds: seconds));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(seconds, (await response.ReadJsonAsync())["misfire_window_seconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Create_DuplicateNameInSameTenant_IsConflict()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/admin/triggers", Body("same"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/triggers", Body("same"))).StatusCode);
    }

    // §1's one-shot reschedule path is "cancel then recreate" (no in-place update route): a
    // cancelled trigger must free its name back up, or that documented path 409s forever.
    [Fact]
    public async Task Create_SameNameAfterCancel_IsCreated()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);
        var created = await (await client.PostAsJsonAsync("/api/admin/triggers", Body("recreate-me"))).ReadJsonAsync();
        var id = created["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await Cancel(client, id, "\"1\"")).StatusCode);

        var recreated = await client.PostAsJsonAsync("/api/admin/triggers", Body("recreate-me"));

        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
        var body = await recreated.ReadJsonAsync();
        Assert.NotEqual(id, body["id"]!.GetValue<string>());
        Assert.Equal("scheduled", body["status"]!.GetValue<string>());
    }

    // Tenant isolation is a security invariant, not a comment: another tenant can neither list nor
    // fetch nor cancel nor read the fire history of this trigger.
    [Fact]
    public async Task Trigger_IsInvisibleToAnotherTenant()
    {
        var tenant = UniqueTenant();
        using var owner = Client(tenant);
        var created = await (await owner.PostAsJsonAsync("/api/admin/triggers", Body("isolated"))).ReadJsonAsync();
        var id = created["id"]!.GetValue<string>();

        using var other = Client(UniqueTenant());
        Assert.Empty((await (await other.GetAsync("/api/admin/triggers")).ReadJsonAsync())["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/admin/triggers/{id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/admin/triggers/{id}/occurrences")).StatusCode);
        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/triggers/{id}/cancel");
        cancel.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(cancel)).StatusCode);
    }

    [Fact]
    public async Task Cancel_RequiresIfMatch_RejectsStaleVersion_AndIsNotRepeatable()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);
        var created = await (await client.PostAsJsonAsync("/api/admin/triggers", Body("cancel-me"))).ReadJsonAsync();
        var id = created["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Cancel(client, id, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(client, id, "\"99\"")).StatusCode);

        var cancelled = await Cancel(client, id, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", (await cancelled.ReadJsonAsync())["status"]!.GetValue<string>());
        Assert.Equal("\"2\"", cancelled.Headers.ETag?.ToString());

        // Already cancelled: the fresh ETag no longer buys a second cancel.
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(client, id, "\"2\"")).StatusCode);
    }

    // The list row must already carry the optimistic-lock version, or a client can only cancel from
    // a list after an extra GET per row just to read its ETag header.
    [Fact]
    public async Task List_CarriesTheVersionCancelRequires()
    {
        using var client = Client(UniqueTenant());
        var created = await (await client.PostAsJsonAsync("/api/admin/triggers", Body("from-list"))).ReadJsonAsync();

        var listed = Assert.Single(
            (await (await client.GetAsync("/api/admin/triggers")).ReadJsonAsync())["items"]!.AsArray())!.AsObject();

        Assert.Equal(created["version"]!.GetValue<long>(), listed["version"]!.GetValue<long>());
        var cancelled = await Cancel(client, listed["id"]!.GetValue<string>(), $"\"{listed["version"]}\"");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var body = await cancelled.ReadJsonAsync();
        Assert.Equal("cancelled", body["status"]!.GetValue<string>());
        Assert.Equal(2, body["version"]!.GetValue<long>());
        Assert.Equal("\"2\"", cancelled.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Occurrences_ExposeOnlyAllowlistedFields_AndOnePendingRowPerOneShotTrigger()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);
        var created = await (await client.PostAsJsonAsync("/api/admin/triggers", Body("history"))).ReadJsonAsync();
        var id = created["id"]!.GetValue<string>();

        var page = await (await client.GetAsync($"/api/admin/triggers/{id}/occurrences")).ReadJsonAsync();

        var item = Assert.Single(page["items"]!.AsArray())!.AsObject();
        Assert.Equal(
            new[] { "created_at", "id", "root_run_id", "scheduled_for", "status", "trigger_id", "updated_at" },
            item.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("pending", item["status"]!.GetValue<string>());
        Assert.Null(item["root_run_id"]);
        Assert.False(page["has_more"]!.GetValue<bool>());
        Assert.Null(page["next_cursor"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Occurrences_RejectLimitOutOfRange(int limit)
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);
        var created = await (await client.PostAsJsonAsync("/api/admin/triggers", Body("limits"))).ReadJsonAsync();

        var response = await client.GetAsync(
            $"/api/admin/triggers/{created["id"]!.GetValue<string>()}/occurrences?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Occurrences_RejectMalformedCursor()
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);
        var created = await (await client.PostAsJsonAsync("/api/admin/triggers", Body("cursor"))).ReadJsonAsync();

        var response = await client.GetAsync(
            $"/api/admin/triggers/{created["id"]!.GetValue<string>()}/occurrences?cursor=not-base64!!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // O5 §6 deliberately has no update route: rescheduling is cancel + recreate, which keeps the
    // pinned target and principal snapshot immutable for a trigger's whole life.
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Trigger_HasNoInPlaceUpdateRoute(string method)
    {
        var tenant = UniqueTenant();
        using var client = Client(tenant);
        var created = await (await client.PostAsJsonAsync("/api/admin/triggers", Body("no-update"))).ReadJsonAsync();

        using var request = new HttpRequestMessage(
            new HttpMethod(method), $"/api/admin/triggers/{created["id"]!.GetValue<string>()}");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Cancel(HttpClient client, string id, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/triggers/{id}/cancel");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        return client.SendAsync(request);
    }

    private static object Mapping(int entries)
    {
        var mapping = new JsonObject { ["message"] = "go" };
        for (var i = 1; i < entries; i++)
        {
            mapping["k" + i] = "v";
        }
        return mapping;
    }

    private static object Body(
        string name = "nightly-report",
        Guid? orchestratorId = null,
        int revision = PublishedRevision,
        int? misfireWindowSeconds = null,
        object? mapping = null,
        DateTime? fireAt = null)
        => new
        {
            name,
            description = "每日夜間報表",
            orchestrator_id = orchestratorId ?? PublishedId,
            orchestrator_revision = revision,
            fire_at = fireAt ?? FireAt,
            misfire_window_seconds = misfireWindowSeconds,
            input_mapping = mapping ?? new { message = "產生報表" },
        };

    private HttpClient Client(string tenant) => Internal(_factory, tenant).WithCapabilities("workflow.manage");

    private static HttpClient Internal(TestWebAppFactory factory, string tenant)
        => factory.CreateInternalClient().WithTenant(tenant).WithUser("op-a").WithRole("ADMIN");

    private static string UniqueTenant() => "trigger-" + Guid.NewGuid().ToString("N");

    public sealed class Factory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("AGENT_TRIGGERS_ENABLED", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITriggerRepository>();
                services.AddSingleton<ITriggerRepository, InMemoryTriggerRepository>();
                services.RemoveAll<IOrchestratorRepository>();
                services.AddSingleton<IOrchestratorRepository, StubTargetOrchestrators>();
            });
        }
    }

    /// <summary>
    /// Three fixed targets covering the whole preflight decision table (published / disabled /
    /// published-revision-absent), plus "unknown id" for everything else. Immutable on purpose: the
    /// fixture is shared, so a mutable stub would make these tests order-dependent.
    /// </summary>
    internal sealed class StubTargetOrchestrators : IOrchestratorRepository
    {
        public Task<Orchestrator?> GetAsync(string tenant, Guid id, CancellationToken ct)
        {
            if (id == PublishedId) return Task.FromResult<Orchestrator?>(Row(id, true, PublishedRevision));
            if (id == DisabledId) return Task.FromResult<Orchestrator?>(Row(id, false, PublishedRevision));
            if (id == UnpublishedId) return Task.FromResult<Orchestrator?>(Row(id, true, null));
            return Task.FromResult<Orchestrator?>(null);
        }

        private static Orchestrator Row(Guid id, bool enabled, int? published)
            => new(id, "root", "", enabled, 1, null, published, "{}", DateTime.UtcNow, DateTime.UtcNow);

        public Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> CreateAsync(string a, string b, string c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> UpdateAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException();
        public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> PublishAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string a, Guid b, CancellationToken c) => throw new NotSupportedException();
        public Task<string?> RevisionAsync(string a, Guid b, int c, CancellationToken d) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> RestoreAsync(string a, Guid b, int c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }
}
