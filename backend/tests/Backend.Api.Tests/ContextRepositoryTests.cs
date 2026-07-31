using System.Text;
using System.Text.Json;
using System.Data;
using Backend.Api.Common;
using Backend.Api.Contexts;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Tests;

[Collection("Postgres")]
public sealed class ContextRepositoryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ParityTenant = "ctxparity-t";
    private const string ParityOtherTenant = "ctxparity-other";

    public Task InitializeAsync() => CleanupAsync();

    public Task DisposeAsync() => CleanupAsync();

    [Fact]
    public void Dapper_RevisionProjection_MapsNullablePostgresColumnsWithoutConstructorMatching()
    {
        var table = new DataTable();
        table.Columns.Add("contextid", typeof(Guid));
        table.Columns.Add("revision", typeof(int));
        table.Columns.Add("rootrunid", typeof(Guid));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("readiness", typeof(decimal));
        table.Columns.Add("policyid", typeof(Guid));
        table.Columns.Add("selectedsourceid", typeof(string));
        table.Columns.Add("adapterid", typeof(string));
        table.Columns.Add("canonical", typeof(byte[]));
        table.Columns.Add("sha", typeof(string));
        table.Columns.Add("unmetrequirements", typeof(string));
        table.Columns.Add("asof", typeof(DateTime));
        table.Columns.Add("createdat", typeof(DateTime));
        table.Columns.Add("expiresat", typeof(DateTime));
        var contextId = Guid.NewGuid(); var policyId = Guid.NewGuid(); var now = DateTime.UtcNow;
        table.Rows.Add(contextId, 1, DBNull.Value, ContextStatuses.Ready, 1m, policyId, "backend_documents",
            "backend.retrieval_search", new byte[] { 1 }, "sha", "[]", now, now, DBNull.Value);

        using var reader = table.CreateDataReader();
        var parser = reader.GetRowParser<ContextRevisionRow>();
        Assert.True(reader.Read());
        var row = parser(reader);

        Assert.Equal(contextId, row.ContextId);
        Assert.Null(row.RootRunId);
        Assert.Null(row.ExpiresAt);
        Assert.Equal("backend_documents", row.SelectedSourceId);
    }

    public static TheoryData<string> ContextProviders => new() { "inmemory", "dapper" };

    // A-CTX-18:lite 與 PostgreSQL 兩條路徑跑同一組不變量 —— revision 不可變、tenant 隔離、
    // 每 tenant 只有一份 active policy。第三條在兩份實作上原本都零覆蓋:PostgreSQL 靠
    // uq_context_policy_active partial index 拒絕第二份,in-memory 必須表現一致(拒絕,不取代)。
    [SkippableTheory]
    [MemberData(nameof(ContextProviders))]
    public async Task A_CTX_18_RevisionsAreImmutable_TenantIsolated_AndOneActivePolicyPerTenant(string provider)
    {
        if (provider == "dapper") fixture.SkipIfUnavailable();
        var (repository, addSecondActivePolicy) = await ProviderAsync(provider);
        var contextId = Guid.NewGuid();
        var first = await repository.CreateRevisionAsync(ParityTenant, "operator", contextId, ParityRequest("first"), default);
        var second = await repository.CreateRevisionAsync(ParityTenant, "operator", contextId, ParityRequest("second"), default);

        Assert.Equal(1, first.Revision.Revision);
        Assert.Equal(2, second.Revision.Revision);
        var reread = await repository.GetRevisionAsync(ParityTenant, contextId, 1, default);
        Assert.Equal(first.Revision.Definition.GetRawText(), reread!.Definition.GetRawText());
        Assert.Contains("\"first\"", reread.Definition.GetRawText());

        Assert.Null(await repository.GetRevisionAsync(ParityOtherTenant, contextId, 1, default));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateRevisionAsync(ParityOtherTenant, "operator", contextId, ParityRequest("cross"), default));

        var active = await repository.GetActivePolicyAsync(ParityTenant, default);
        Assert.NotNull(active);
        await Assert.ThrowsAnyAsync<Exception>(addSecondActivePolicy);
        Assert.Equal(active!.Id, (await repository.GetActivePolicyAsync(ParityTenant, default))!.Id);
    }

    [Fact]
    public async Task InMemory_ReadyRevision_ProjectsPlannerViewAndRoleScopedTaskEnvelope()
    {
        var rag = new InMemoryRagRepository();
        var documentId = Guid.NewGuid();
        await rag.InsertProcessingDocumentAsync(documentId.ToString("D"), "demo-a", "document", default);
        await rag.CompleteDocumentAsync(documentId.ToString("D"), "demo-a", ["evidence"], [new[] { 1f }], default);
        var chunk = Assert.Single(await rag.SearchAsync("demo-a", [1f], 1, default));
        IContextRepository repository = new InMemoryContextRepository(rag);
        var rootId = Guid.NewGuid();
        ((IContextAuthorityRegistry)repository).RegisterRoot("demo-a", "user-a", rootId, RootSnapshot(documentId));
        var contextId = Guid.NewGuid();
        var request = Request(rootId, documentId, Guid.Parse(chunk.ChunkId), "evidence");

        var first = await repository.CreateRevisionAsync("demo-a", "user-a", contextId, request, default);

        Assert.Equal(ContextStatuses.Ready, first.Revision.Status);
        Assert.NotNull(first.Revision.ContextRef);
        Assert.Equal("backend_documents", first.Revision.SelectedSourceId);
        Assert.Equal("backend.retrieval_search", first.Revision.AdapterId);
        Assert.Equal("planner", (await repository.GetViewAsync("demo-a", first.Revision.ContextRef!.ViewId, default))!.ViewType);

        var wrongType = request with { Evidence = [Evidence(documentId, Guid.Parse(chunk.ChunkId), "evidence") with { EvidenceType = "metric" }] };
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateRevisionAsync("demo-a", "user-a", Guid.NewGuid(), wrongType, default));

        var callerEnvelope = JsonSerializer.SerializeToElement(new { objective = "analyze", required_capabilities = Array.Empty<string>(), context = new { caller = "forged" }, context_provenance = new[] { new { context_key = "caller", source_type = "caller", source_id = "caller", observed_at = DateTime.UtcNow.ToString("O"), content_sha256 = new string('b', 64) } }, write_intent = false, delegation_depth = 0 });
        var projected = ContextTaskEnvelopeProjection.Apply(callerEnvelope, first, "worker");
        Assert.True(projected.TryGetProperty("context_ref", out var projectedRef));
        Assert.Equal(first.Revision.ContextRef.ContextId.ToString("D"), projectedRef.GetProperty("context_id").GetString());
        Assert.False(projected.GetProperty("context").TryGetProperty("caller", out _));
        Assert.Equal("worker", projected.GetProperty("context").GetProperty("role").GetString());
        Assert.All(projected.GetProperty("context_provenance").EnumerateArray(), item => Assert.Equal("context-tool", item.GetProperty("source_type").GetString()));
        Assert.Equal("verifier", ContextTaskEnvelopeProjection.Apply(callerEnvelope, first, "verifier").GetProperty("context").GetProperty("role").GetString());
        Assert.Equal("synthesizer", ContextTaskEnvelopeProjection.Apply(callerEnvelope, first, "synthesizer").GetProperty("context").GetProperty("role").GetString());
    }

    // M1:同租戶的另一個 user 既不能把 revision 掛到別人的 root run 上,也讀不到那個 run 的 ready context。
    [Fact]
    public async Task InMemory_RootRunOwnership_IsUserScopedWithinTheSameTenant()
    {
        var rag = new InMemoryRagRepository();
        var documentId = Guid.NewGuid();
        await rag.InsertProcessingDocumentAsync(documentId.ToString("D"), "demo-a", "document", default);
        await rag.CompleteDocumentAsync(documentId.ToString("D"), "demo-a", ["evidence"], [new[] { 1f }], default);
        var chunk = Assert.Single(await rag.SearchAsync("demo-a", [1f], 1, default));
        IContextRepository repository = new InMemoryContextRepository(rag);
        var rootId = Guid.NewGuid();
        ((IContextAuthorityRegistry)repository).RegisterRoot("demo-a", "user-a", rootId, RootSnapshot(documentId));
        var request = Request(rootId, documentId, Guid.Parse(chunk.ChunkId), "evidence");
        await repository.CreateRevisionAsync("demo-a", "user-a", Guid.NewGuid(), request, default);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateRevisionAsync("demo-a", "user-b", Guid.NewGuid(), request, default));
        Assert.Null(await repository.GetLatestReadyForRunAsync("demo-a", "user-b", rootId, default));
        Assert.NotNull(await repository.GetLatestReadyForRunAsync("demo-a", "user-a", rootId, default));
    }

    [Fact]
    public void Readiness_AllStatusesAndThresholdsArePolicyDriven()
    {
        var evidence = new[] { Evidence() };
        using var permissive = Policy("0.80", "0.70");
        using var strict = Policy("0.95", "0.85");

        Assert.Equal(ContextStatuses.Ready, ReadinessEvaluator.Evaluate(evidence, permissive.RootElement, Measurements(), "backend_documents").Status);
        Assert.Equal(ContextStatuses.Ready, ReadinessEvaluator.Evaluate(evidence, strict.RootElement, Measurements(), "backend_documents").Status);

        var hardGate = ReadinessEvaluator.Evaluate([], permissive.RootElement, Measurements());
        Assert.Equal(ContextStatuses.NeedMoreContext, hardGate.Status);
        Assert.Contains("document", hardGate.Unmet);
        Assert.Equal(ContextStatuses.InsufficientData, ReadinessEvaluator.Evaluate([], permissive.RootElement, Measurements(round: 2, max: 2)).Status);
        Assert.Equal(ContextStatuses.NeedsClarification, ReadinessEvaluator.Evaluate([], permissive.RootElement, Measurements(ambiguity: true)).Status);
        Assert.Equal(ContextStatuses.BlockedByPolicy, ReadinessEvaluator.Evaluate(evidence, permissive.RootElement, Measurements(violations: ["denied-source"]), "backend_documents").Status);
        Assert.Equal(ContextStatuses.NeedMoreContext, ReadinessEvaluator.Evaluate(evidence, permissive.RootElement, Measurements(gaps: [new("backend_documents", "timeout")]), "backend_documents").Status);
        Assert.Equal(ContextStatuses.Ready, ReadinessEvaluator.Evaluate(evidence, permissive.RootElement, Measurements(gaps: [new("optional_source", "timeout")]), "backend_documents").Status);
    }

    // ECT:量測值帶來的 source failure 若指向政策未宣告的 source(或 source_id/failure_code 空白),
    // 是「無法判定」而不是可扣分的 gap —— 在任何評分之前就短路成 BLOCKED_BY_POLICY / 0 分,
    // 且 unmet 退化成全部 mandatory requirement(有別於 policy_violations 那條 BLOCKED 路徑)。
    [Theory]
    [InlineData("unknown_source", "timeout")]
    [InlineData(null, "timeout")]
    [InlineData("backend_documents", "  ")]
    public void Readiness_UnrecognizedOrBlankSourceFailure_ShortCircuitsToBlockedByPolicy(string? sourceId, string failureCode)
    {
        using var policy = Policy("0.80", "0.70");

        var decision = ReadinessEvaluator.Evaluate([Evidence()], policy.RootElement,
            Measurements(gaps: [new(sourceId, failureCode)]), "backend_documents");

        Assert.Equal(ContextStatuses.BlockedByPolicy, decision.Status);
        Assert.Equal(0m, decision.Readiness);
        Assert.Equal(new[] { "document" }, decision.Unmet);
    }

    // M6/L4:判定器的兩種「不合法」必須分流 —— 候選量測值是呼叫者的錯(ArgumentException → 400),
    // 政策本身壞掉(缺鍵、未知鍵)是伺服器資產的錯(ContextPolicyInvalidException → 422)。
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"readiness\":{\"ready_threshold\":0.8,\"assumptions_min\":0.7,\"optional_failure_penalty\":0.1},\"bootstrap_requirements\":[{\"name\":\"document\",\"evidence_type\":\"document\",\"mandatory\":true}],\"source_requirements\":[{\"source_id\":\"backend_documents\",\"required\":true}],\"requirement_templates\":{}}")]
    public void Readiness_MalformedOrUnknownKeyedPolicy_IsAPolicyFailure(string policyJson)
    {
        using var policy = JsonDocument.Parse(policyJson);
        Assert.Throws<ContextPolicyInvalidException>(() => ReadinessEvaluator.Evaluate([Evidence()], policy.RootElement, Measurements()));
    }

    // BVT:政策數值域的 on-point / off-point。形狀正確、鍵也全對,但門檻越界的政策一樣不是可用政策
    // ——(0,1] 的 ready_threshold、[0,ready_threshold) 的 assumptions_min,越界即 422。
    [Theory]
    [InlineData("0", "0", false)]        // ready_threshold 下界 on-point:0 不合法
    [InlineData("0.01", "0", true)]      // 剛好越過下界;assumptions_min 下界 0 本身合法
    [InlineData("1", "0.70", true)]      // ready_threshold 上界 on-point:1 合法
    [InlineData("1.01", "0.70", false)]  // 剛好越過上界
    [InlineData("0.80", "0.80", false)]  // assumptions_min 不得等於 ready_threshold
    [InlineData("0.80", "0.79", true)]   // 剛好小於即合法
    [InlineData("0.80", "-0.01", false)] // assumptions_min 跌破 0
    public void Readiness_PolicyThresholdsOutsideTheirRange_IsAPolicyFailure(string ready, string assumptions, bool valid)
    {
        using var policy = Policy(ready, assumptions);

        var error = Record.Exception(() => ReadinessEvaluator.ValidatePolicy(policy.RootElement));

        if (valid) Assert.Null(error); else Assert.IsType<ContextPolicyInvalidException>(error);
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(3, 2, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 2, -1)]
    public void Readiness_InvalidMeasurements_IsACallerFailure(int round, int max, int assumptions)
    {
        using var policy = Policy("0.80", "0.70");
        var measurements = new ContextObjectiveMeasurements(round, max, false, false, assumptions, null, []);
        Assert.Throws<ArgumentException>(() => ReadinessEvaluator.Evaluate([Evidence()], policy.RootElement, measurements, "backend_documents"));
        Assert.Throws<ArgumentException>(() => ReadinessEvaluator.Evaluate([Evidence()], policy.RootElement, null, "backend_documents"));
    }

    // A-CTX-22(I2 守門):evidence 與量測值完全固定,只把 ready_threshold 由 0.70 調到 0.80,
    // status 必須跟著翻面。分數 0.75 是 4 個 requirement 覆蓋 3 個算出來的,不是二元值。
    [Fact]
    public void Readiness_A_CTX_22_RaisingOnlyTheThreshold_FlipsStatus()
    {
        var evidence = Covering("document", "metric", "entity");
        var measurements = Measurements();
        using var permissive = CoveragePolicy("0.70", "0.60");
        using var strict = CoveragePolicy("0.80", "0.60");

        var before = ReadinessEvaluator.Evaluate(evidence, permissive.RootElement, measurements, "backend_documents");
        var after = ReadinessEvaluator.Evaluate(evidence, strict.RootElement, measurements, "backend_documents");

        Assert.Equal(0.75m, before.Readiness);
        Assert.Equal(before.Readiness, after.Readiness);
        Assert.Empty(before.Unmet);
        Assert.Equal(after.Unmet, before.Unmet);
        Assert.Equal(ContextStatuses.Ready, before.Status);
        Assert.Equal(ContextStatuses.NeedMoreContext, after.Status);
    }

    // M4:READY_WITH_ASSUMPTIONS 必須真的可達 —— 分數落在 [assumptions_min, ready_threshold) 且
    // assumptions_count ≥ 1;少了 assumptions 就退回 NEED_MORE_CONTEXT,不得靜默升級。
    [Fact]
    public void Readiness_ReadyWithAssumptions_IsReachableInsideThePolicyBand()
    {
        var evidence = Covering("document", "metric", "entity");
        using var policy = CoveragePolicy("0.80", "0.70");

        var granted = ReadinessEvaluator.Evaluate(evidence, policy.RootElement, Measurements(assumptions: 1), "backend_documents");
        var withoutAssumptions = ReadinessEvaluator.Evaluate(evidence, policy.RootElement, Measurements(), "backend_documents");

        Assert.Equal(ContextStatuses.ReadyWithAssumptions, granted.Status);
        Assert.Equal(0.75m, granted.Readiness);
        Assert.Empty(granted.Unmet);
        Assert.Equal(ContextStatuses.NeedMoreContext, withoutAssumptions.Status);
    }

    // M4 的持久化半邊(02-spec §2):只有 envelope 的 assumptions 非空、與 assumptions_count 一致、
    // 且出現在 planner view 時才授予 READY_WITH_ASSUMPTIONS;不一致的候選是 400,不是降級。
    [Fact]
    public async Task InMemory_ReadyWithAssumptions_RequiresAssumptionsMatchingTheMeasuredCountAndPlannerView()
    {
        var rag = new InMemoryRagRepository();
        var documentId = Guid.NewGuid();
        await rag.InsertProcessingDocumentAsync(documentId.ToString("D"), "assume-a", "document", default);
        await rag.CompleteDocumentAsync(documentId.ToString("D"), "assume-a", ["evidence"], [new[] { 1f }], default);
        var chunk = Assert.Single(await rag.SearchAsync("assume-a", [1f], 1, default));
        var repository = new InMemoryContextRepository(rag, policyTenants: []);
        using var band = CoveragePolicy("0.30", "0.20", documentMandatoryOnly: true);
        repository.SeedActivePolicy("assume-a", band.RootElement.Clone(),
            [new("backend_documents", "document", "knowledge-source", "server-owned", "backend.retrieval_search", true, true, 15, 2)]);
        var rootId = Guid.NewGuid();
        ((IContextAuthorityRegistry)repository).RegisterRoot("assume-a", "user-a", rootId, RootSnapshot(documentId));
        var evidence = Evidence(documentId, Guid.Parse(chunk.ChunkId), "evidence");
        ContextRevisionSubmitRequest Submit(string assumptions, string plannerView) => new(rootId,
            JsonDocument.Parse($"{{\"assumptions\":{assumptions}}}").RootElement.Clone(), [evidence],
            [new("planner", JsonDocument.Parse(plannerView).RootElement.Clone())], Measurements(assumptions: 1));

        var granted = await repository.CreateRevisionAsync("assume-a", "user-a", Guid.NewGuid(),
            Submit("[\"freshness assumed\"]", "{\"assumptions\":[\"freshness assumed\"]}"), default);
        Assert.Equal(ContextStatuses.ReadyWithAssumptions, granted.Revision.Status);
        Assert.NotNull(granted.Revision.ContextRef);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateRevisionAsync("assume-a", "user-a", Guid.NewGuid(),
            Submit("[]", "{\"assumptions\":[\"freshness assumed\"]}"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateRevisionAsync("assume-a", "user-a", Guid.NewGuid(),
            Submit("[\"freshness assumed\"]", "{\"facts\":[]}"), default));
    }

    // M5:權威 canonical bytes 是 Context 讀取路徑唯一的真相來源,竄改一律 fail closed。
    [Theory]
    [InlineData("hash-mismatch")]
    [InlineData("bom")]
    [InlineData("non-canonical")]
    public void ReadAuthoritative_TamperedCanonicalBytes_FailClosed(string mutation)
    {
        var canonical = Backend.Api.Agents.AgentCanonicalizer.CanonicalizeDefinition("{\"b\":1,\"a\":2}");
        Assert.Equal(canonical, ContextCanonicalizer.ReadAuthoritative(Encoding.UTF8.GetBytes(canonical), SkillHash.Sha256(canonical), "Context"));

        byte[] bytes = mutation switch
        {
            "bom" => [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(canonical)],
            "non-canonical" => Encoding.UTF8.GetBytes("{\"b\":1,\"a\":2}"),
            _ => Encoding.UTF8.GetBytes(canonical),
        };
        var sha = mutation == "hash-mismatch" ? new string('0', 64) : SkillHash.Sha256(bytes);

        Assert.Throws<InvalidOperationException>(() => ContextCanonicalizer.ReadAuthoritative(bytes, sha, "Context"));
    }

    // ECT:evidence 自身的拒絕條件,在倉儲碰到政策/租戶之前就先擋下 —— 空白識別欄位、非「64 位小寫
    // hex」形狀的 content_hash、重複的 (snapshot_id, content_ref) 全是候選者的錯(ArgumentException)。
    [Theory]
    [InlineData("blank-source", "context evidence is invalid")]
    [InlineData("short-hash", "context evidence is invalid")]
    [InlineData("uppercase-hash", "context evidence is invalid")]
    [InlineData("duplicate-pair", "context evidence is duplicated or too large")]
    public void ValidateEvidence_MalformedOrDuplicatedCandidate_IsRejected(string mutation, string message)
    {
        var item = Evidence();
        ContextEvidenceInput[] candidate = mutation switch
        {
            "blank-source" => [item with { SourceId = "   " }],
            "short-hash" => [item with { ContentHash = new string('a', 63) }],
            "uppercase-hash" => [item with { ContentHash = new string('A', 64) }],
            _ => [item, item],
        };

        Assert.Equal(message, Assert.Throws<ArgumentException>(() => ContextCanonicalizer.ValidateEvidence(candidate)).Message);
    }

    // BVT:三個位元組上限各測 on-point 與 off-point —— 剛好等於上限必須收下,多 1 byte 必須擋。
    [Fact]
    public void Canonicalizer_SizeCeilings_AcceptTheLimitAndRejectOneByteOver()
    {
        Assert.NotEmpty(ContextCanonicalizer.CanonicalizeDefinition(DefinitionOfBytes(ContextCanonicalizer.MaxDefinitionBytes)));
        Assert.Equal("definition exceeds 256 KB", Assert.Throws<ArgumentException>(
            () => ContextCanonicalizer.CanonicalizeDefinition(DefinitionOfBytes(ContextCanonicalizer.MaxDefinitionBytes + 1))).Message);

        Assert.NotEmpty(ContextCanonicalizer.CanonicalizeView(DefinitionOfBytes(ContextCanonicalizer.MaxEvidenceBytes)));
        Assert.Equal("view definition exceeds 64 KB", Assert.Throws<ArgumentException>(
            () => ContextCanonicalizer.CanonicalizeView(DefinitionOfBytes(ContextCanonicalizer.MaxEvidenceBytes + 1))).Message);

        ContextCanonicalizer.ValidateEvidence([Evidence() with { ContentRef = new string('r', 1_024) }]);
        Assert.Equal("context evidence is duplicated or too large", Assert.Throws<ArgumentException>(
            () => ContextCanonicalizer.ValidateEvidence([Evidence() with { ContentRef = new string('r', 1_025) }])).Message);
    }

    [Fact]
    public async Task InMemory_RejectsLowerPrecedenceAdapterLineage_WhenEvidenceTypesMatch()
    {
        var rag = new InMemoryRagRepository();
        var documentId = Guid.NewGuid();
        await rag.InsertProcessingDocumentAsync(documentId.ToString("D"), "demo-a", "document", default);
        await rag.CompleteDocumentAsync(documentId.ToString("D"), "demo-a", ["evidence"], [new[] { 1f }], default);
        var chunk = Assert.Single(await rag.SearchAsync("demo-a", [1f], 1, default));
        ContextSourceCatalogEntry[] sources =
        [
            new("preferred_documents", "document", "knowledge-source", "server-owned", "backend.preferred_search", true, true, 15, 2),
            new("fallback_documents", "document", "knowledge-source", "server-owned", "backend.fallback_search", true, false, 15, 2)
        ];
        IContextRepository repository = new InMemoryContextRepository(rag, sources);
        var rootId = Guid.NewGuid();
        ((IContextAuthorityRegistry)repository).RegisterRoot("demo-a", "user-a", rootId, JsonSerializer.Serialize(new
        {
            authority = new
            {
                knowledge_sources = new[] { documentId.ToString("D") },
                context_tools = new[] { "backend.preferred_search", "backend.fallback_search" }
            }
        }));
        var forged = Evidence(documentId, Guid.Parse(chunk.ChunkId), "evidence") with
        {
            Lineage = JsonSerializer.SerializeToElement(new { catalog_source_id = "fallback_documents", adapter_id = "backend.fallback_search" })
        };
        var request = new ContextRevisionSubmitRequest(rootId, JsonSerializer.SerializeToElement(new { }), [forged],
            [new("planner", JsonSerializer.SerializeToElement(new { }))], Measurements());

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateRevisionAsync("demo-a", "user-a", Guid.NewGuid(), request, default));

        var valid = forged with { Lineage = JsonSerializer.SerializeToElement(new { catalog_source_id = "preferred_documents", adapter_id = "backend.preferred_search" }) };
        var accepted = await repository.CreateRevisionAsync("demo-a", "user-a", Guid.NewGuid(), request with { Evidence = [valid] }, default);
        Assert.Equal("preferred_documents", accepted.Revision.SelectedSourceId);
        Assert.Equal("backend.preferred_search", accepted.Revision.AdapterId);
    }

    [Fact]
    public void AcquireProjection_HasExactContextProvenanceKeyParity()
    {
        var contextId = Guid.NewGuid(); var viewId = Guid.NewGuid();
        var contextRef = new ContextRef(contextId, 1, viewId);
        var definition = JsonSerializer.SerializeToElement(new { facts = new { value = 1 } });
        var revision = new ContextRevisionResponse(contextId, 1, Guid.NewGuid(), ContextStatuses.Ready, 1m, [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new { }), DateTime.UtcNow, DateTime.UtcNow, null, contextRef);
        var response = ContextAcquireProjection.Build(new ContextStoredRevision(revision, [], [new(viewId, contextId, 1, "planner", definition)], "backend.retrieval_search"));

        Assert.NotNull(response);
        var keys = response!.Context.EnumerateObject().Select(x => x.Name).Order().ToArray();
        var provenanceKeys = response.Provenance.Select(x => x.GetProperty("context_key").GetString()).Order().ToArray();
        Assert.Equal(new[] { "context_ref", "view" }, keys);
        Assert.Equal(keys, provenanceKeys);
        Assert.All(response.Provenance, item =>
        {
            Assert.Equal("context-tool", item.GetProperty("source_type").GetString());
            Assert.Equal("backend.retrieval_search", item.GetProperty("source_id").GetString());
            Assert.Matches("^[0-9a-f]{64}$", item.GetProperty("content_sha256").GetString()!);
        });
        var forged = JsonSerializer.SerializeToElement(new { context_ref = contextRef });
        Assert.Throws<ArgumentException>(() => ContextTaskEnvelopeProjection.ApplyIfAvailable(forged, null, "worker"));
    }

    // ECT:role-scoped 投影只認 worker/verifier/synthesizer。planner 是伺服器內部視圖,大小寫不同
    // 或空白也都不是合法角色 —— 一律擋下,不得靜默落回任何預設視圖。
    [Theory]
    [InlineData("planner")]
    [InlineData("Worker")]
    [InlineData("")]
    public void TaskEnvelopeProjection_InvalidViewRole_IsRejected(string viewType)
    {
        var contextId = Guid.NewGuid(); var viewId = Guid.NewGuid();
        var revision = new ContextRevisionResponse(contextId, 1, Guid.NewGuid(), ContextStatuses.Ready, 1m, [], Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { }), DateTime.UtcNow, DateTime.UtcNow, null, new ContextRef(contextId, 1, viewId));
        var stored = new ContextStoredRevision(revision, [],
            [new(viewId, contextId, 1, "planner", JsonSerializer.SerializeToElement(new { facts = 1 }))], "backend.retrieval_search");

        var error = Assert.Throws<ArgumentException>(() => ContextTaskEnvelopeProjection.Apply(
            JsonSerializer.SerializeToElement(new { objective = "analyze" }), stored, viewType));

        Assert.Equal("Context view role is invalid", error.Message);
    }

    // A-CTX-05:跨租戶注入的是**另一租戶真實存在**的文件與 chunk,content_hash 也對得上;
    // 唯一擋下它的是 chunk 查詢的 tenant 述詞,必須 hard fail 而不是降級成 gap。
    [Fact]
    public async Task InMemory_RejectsCrossTenantContextIdAndRealForeignTenantChunk()
    {
        var rag = new InMemoryRagRepository();
        var foreignDocument = Guid.NewGuid();
        await rag.InsertProcessingDocumentAsync(foreignDocument.ToString("D"), "demo-b", "tenant-b", default);
        await rag.CompleteDocumentAsync(foreignDocument.ToString("D"), "demo-b", ["tenant-b secret"], [new[] { 1f }], default);
        var foreignChunk = Assert.Single(await rag.SearchAsync("demo-b", [1f], 1, default));
        var repository = new InMemoryContextRepository(rag);
        var contextId = Guid.NewGuid();
        var empty = new ContextRevisionSubmitRequest(null, JsonSerializer.SerializeToElement(new { }), [], [], Measurements: Measurements());
        await repository.CreateRevisionAsync("demo-a", "user-a", contextId, empty, default);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateRevisionAsync("demo-b", "user-b", contextId, empty, default));

        var rootId = Guid.NewGuid();
        ((IContextAuthorityRegistry)repository).RegisterRoot("demo-a", "user-a", rootId, RootSnapshot(foreignDocument));
        var injected = Evidence(foreignDocument, Guid.Parse(foreignChunk.ChunkId), "tenant-b secret");
        var request = new ContextRevisionSubmitRequest(rootId, JsonSerializer.SerializeToElement(new { }), [injected],
            [new("planner", JsonSerializer.SerializeToElement(new { }))], Measurements());

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateRevisionAsync("demo-a", "user-a", Guid.NewGuid(), request, default));

        var unknownDocument = Guid.NewGuid();
        var fabricated = new ContextEvidenceInput("document", unknownDocument.ToString("D"), "snapshot", $"document://{unknownDocument:D}#chunk/{Guid.NewGuid():D}", new string('a', 64));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateRevisionAsync("demo-a", "user-a", Guid.NewGuid(), request with { Evidence = [fabricated] }, default));
    }

    // L5/M1:Context 端點的狀態碼契約 —— 無 active policy 一律 503(與 E3 delta 一致,不是 409),
    // 缺 X-User-Id 是 400,GET view 回 revision 的 ETag,未知 view 不洩漏存在性。
    [Fact]
    public async Task ContextController_PolicyUnavailableIs503_UserIsRequired_AndViewCarriesARevisionEtag()
    {
        var repository = new InMemoryContextRepository(policyTenants: ["demo-a"]);
        var unknownTenant = Controller(repository, "no-policy-tenant", "user-a", out _);
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => unknownTenant.GetPolicy(default))).Status);
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => unknownTenant.Submit(Guid.NewGuid(), ParityRequest("blocked"), default))).Status);

        var anonymous = Controller(repository, "demo-a", null, out _);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => anonymous.Submit(Guid.NewGuid(), ParityRequest("anonymous"), default))).Status);

        var stored = await repository.CreateRevisionAsync("demo-a", "user-a", Guid.NewGuid(), ParityRequest("view"), default);
        var owner = Controller(repository, "demo-a", "user-a", out var http);
        Assert.IsType<OkObjectResult>(await owner.GetView(stored.Views.Single().ViewId, default));
        Assert.Equal("\"1\"", http.Response.Headers.ETag.ToString());
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => owner.GetView(Guid.NewGuid(), default))).Status);
    }

    private static ContextController Controller(IContextRepository repository, string tenant, string? user, out DefaultHttpContext http)
    {
        http = new DefaultHttpContext();
        http.Request.Headers[Backend.Api.Common.IdentityHeaders.TenantHeader] = tenant;
        if (user is not null) http.Request.Headers[Backend.Api.Common.IdentityHeaders.UserHeader] = user;
        return new ContextController(repository) { ControllerContext = new() { HttpContext = http } };
    }

    [Fact]
    public async Task InMemory_ConcurrentCrossTenantFirstWriterHasOneStableRejection()
    {
        IContextRepository repository = new InMemoryContextRepository();
        var contextId = Guid.NewGuid();
        var request = new ContextRevisionSubmitRequest(null, JsonSerializer.SerializeToElement(new { }), [], [], Measurements: Measurements());
        var outcomes = await Task.WhenAll(new[] { "demo-a", "demo-b" }.Select(async tenant =>
        {
            try { await repository.CreateRevisionAsync(tenant, "operator", contextId, request, default); return "created"; }
            catch (ArgumentException) { return "rejected"; }
        }));
        Assert.Equal(1, outcomes.Count(x => x == "created"));
        Assert.Equal(1, outcomes.Count(x => x == "rejected"));
    }

    private async Task<(IContextRepository Repository, Func<Task> AddSecondActivePolicy)> ProviderAsync(string provider)
    {
        if (provider == "inmemory")
        {
            var repository = new InMemoryContextRepository(policyTenants: [ParityTenant, ParityOtherTenant]);
            return (repository, () => Task.Run(() => repository.SeedActivePolicy(ParityTenant, ParityPolicy(), [])));
        }

        foreach (var tenant in new[] { ParityTenant, ParityOtherTenant }) await InsertActivePolicyAsync(tenant, "default");
        return (new ContextRepository(fixture.DataSource!), () => InsertActivePolicyAsync(ParityTenant, "second"));
    }

    private async Task InsertActivePolicyAsync(string tenant, string name)
    {
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO context_policy(id,tenant_id,name,is_active,values,created_by) VALUES(@id,@tenant,@name,true,@values::jsonb,'test')",
            new { id = Guid.NewGuid(), tenant, name, values = ParityPolicy().GetRawText() });
    }

    private async Task CleanupAsync()
    {
        if (!fixture.Available) return;
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM context_view WHERE tenant_id LIKE 'ctxparity-%'; DELETE FROM context_evidence WHERE tenant_id LIKE 'ctxparity-%'; DELETE FROM context_revision WHERE tenant_id LIKE 'ctxparity-%'; DELETE FROM context_policy WHERE tenant_id LIKE 'ctxparity-%'");
    }

    private static JsonElement ParityPolicy() => JsonDocument.Parse(
        "{\"readiness\":{\"ready_threshold\":0.85,\"assumptions_min\":0.70,\"optional_failure_penalty\":0.10}," +
        "\"bootstrap_requirements\":[{\"name\":\"document\",\"evidence_type\":\"document\",\"mandatory\":true}]," +
        "\"source_requirements\":[{\"source_id\":\"backend_documents\",\"required\":true}]," +
        "\"source_precedence\":[\"backend_documents\"]}").RootElement.Clone();

    private static ContextRevisionSubmitRequest ParityRequest(string marker) => new(null,
        JsonDocument.Parse($"{{\"marker\":\"{marker}\"}}").RootElement.Clone(), [],
        [new("planner", JsonSerializer.SerializeToElement(new { role = "planner" }))], Measurements());

    private static string RootSnapshot(Guid documentId) => JsonSerializer.Serialize(new
    {
        authority = new { knowledge_sources = new[] { documentId.ToString("D") }, context_tools = new[] { "backend.retrieval_search" } }
    });

    private static ContextRevisionSubmitRequest Request(Guid rootId, Guid documentId, Guid chunkId, string content)
    {
        var definition = JsonDocument.Parse("{\"requirements\":[{\"name\":\"document\",\"mandatory\":true}],\"covered_requirements\":[\"document\"]}").RootElement.Clone();
        return new(rootId, definition, [Evidence(documentId, chunkId, content)],
            [new("planner", JsonSerializer.SerializeToElement(new { role = "planner" })), new("worker", JsonSerializer.SerializeToElement(new { role = "worker" })), new("verifier", JsonSerializer.SerializeToElement(new { role = "verifier" })), new("synthesizer", JsonSerializer.SerializeToElement(new { role = "synthesizer" }))],
            Measurements: Measurements());
    }

    private static ContextEvidenceInput Evidence(Guid? documentId = null, Guid? chunkId = null, string content = "evidence")
    {
        var document = documentId ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        var chunk = chunkId ?? Guid.Parse("00000000-0000-0000-0000-000000000002");
        return new("document", document.ToString("D"), "snapshot-1", $"document://{document:D}#chunk/{chunk:D}", SkillHash.Sha256(content),
            Observations: JsonSerializer.SerializeToElement(new { completeness = 0.9m }),
            Lineage: JsonSerializer.SerializeToElement(new { catalog_source_id = "backend_documents", adapter_id = "backend.retrieval_search" }));
    }

    /// <summary>原文剛好 <paramref name="totalBytes"/> 個 UTF-8 byte 的 JSON 物件
    /// (<c>{"v":"aaa…"}</c> 外框佔 8 byte),用來壓 canonicalizer 的位元組上限邊界。</summary>
    private static JsonElement DefinitionOfBytes(int totalBytes)
        => JsonDocument.Parse($"{{\"v\":\"{new string('a', totalBytes - 8)}\"}}").RootElement.Clone();

    /// <summary>同一筆 evidence 換上不同 evidence_type,用來覆蓋政策裡的多個 requirement。</summary>
    private static ContextEvidenceInput[] Covering(params string[] evidenceTypes)
        => [.. evidenceTypes.Select(type => Evidence() with { EvidenceType = type })];

    private static ContextObjectiveMeasurements Measurements(int round = 1, int max = 2, bool ambiguity = false, int assumptions = 0, IReadOnlyList<string>? violations = null, IReadOnlyList<ContextSourceFailure>? gaps = null)
        => new(round, max, ambiguity, false, assumptions, violations, gaps ?? []);

    private static JsonDocument Policy(string ready, string assumptions)
        => JsonDocument.Parse($"{{\"readiness\":{{\"ready_threshold\":{ready},\"assumptions_min\":{assumptions},\"optional_failure_penalty\":0.10}},\"bootstrap_requirements\":[{{\"name\":\"document\",\"evidence_type\":\"document\",\"mandatory\":true}}],\"source_requirements\":[{{\"source_id\":\"backend_documents\",\"required\":true}},{{\"source_id\":\"optional_source\",\"required\":false}}]}}");

    /// <summary>4 個 requirement 的政策:分數才有 0.25 粒度的非二元值。<paramref name="documentMandatoryOnly"/>
    /// 讓只有 document 證據的倉儲路徑也能通過 mandatory hard gate。</summary>
    private static JsonDocument CoveragePolicy(string ready, string assumptions, bool documentMandatoryOnly = false)
    {
        var mandatory = documentMandatoryOnly ? "false" : "true";
        return JsonDocument.Parse(
            $"{{\"readiness\":{{\"ready_threshold\":{ready},\"assumptions_min\":{assumptions},\"optional_failure_penalty\":0.10}}," +
            "\"bootstrap_requirements\":[" +
            "{\"name\":\"document\",\"evidence_type\":\"document\",\"mandatory\":true}," +
            $"{{\"name\":\"metric\",\"evidence_type\":\"metric\",\"mandatory\":{mandatory}}}," +
            $"{{\"name\":\"entity\",\"evidence_type\":\"entity\",\"mandatory\":{mandatory}}}," +
            "{\"name\":\"peer\",\"evidence_type\":\"peer\",\"mandatory\":false}]," +
            "\"source_requirements\":[{\"source_id\":\"backend_documents\",\"required\":true},{\"source_id\":\"optional_source\",\"required\":false}]," +
            "\"source_precedence\":[\"backend_documents\"]}");
    }
}
