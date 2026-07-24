using Dapper;
using Npgsql;
using System.Text.Json.Nodes;

namespace Backend.Api.Agents;

/// <summary>
/// Agent aggregate 的 Dapper + Npgsql 實作。snake_case 欄位以 AS alias 對映 PascalCase。
/// draft 是唯一可變作者副本(jsonb),以 draft_version 做 optimistic concurrency;publish/restore
/// 於單一交易內原子完成「supersede 舊 published + 建立不可變 revision + 固定 skill bindings + 更新指標」。
/// publish 直接由 draft_definition jsonb 抽欄落 revision(不在 C# 拆解),definition hash 抄自 draft
/// 落欄的 draft_definition_sha256(對 canonical 文字,不受 jsonb roundtrip 影響)。
/// </summary>
public sealed class AgentRepository : IAgentRepository
{
    private const string AgentCols =
        "id AS Id, slug AS Slug, name AS Name, description AS Description, enabled AS Enabled,"
        + " draft_version AS DraftVersion, draft_validated_version AS DraftValidatedVersion,"
        + " published_revision AS PublishedRevision, draft_definition::text AS DraftDefinition,"
        + " draft_definition_sha256 AS DraftDefinitionSha256, created_at AS CreatedAt, updated_at AS UpdatedAt";

    private readonly NpgsqlDataSource _dataSource;

    public AgentRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<AgentInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AgentInfo>(new CommandDefinition(
            "SELECT id AS Id, slug AS Slug, name AS Name, description AS Description, enabled AS Enabled,"
            + " draft_version AS DraftVersion, draft_validated_version AS DraftValidatedVersion,"
            + " published_revision AS PublishedRevision, created_at AS CreatedAt, updated_at AS UpdatedAt"
            + " FROM agent WHERE tenant_id = @tenantId ORDER BY slug",
            new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<Agent?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var agent = await conn.QuerySingleOrDefaultAsync<Agent>(new CommandDefinition(
            $"SELECT {AgentCols} FROM agent WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
        return WithCanonicalDefinition(agent);
    }

    public async Task<Agent?> CreateAsync(
        string tenantId, string slug, string name, string description,
        string canonicalDefinition, string definitionSha256, string createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // 同 tenant slug 重複 → ON CONFLICT DO NOTHING → 0 列 → null(controller 映射 409)。
        var created = await conn.QuerySingleOrDefaultAsync<Agent>(new CommandDefinition(
            "INSERT INTO agent (tenant_id, slug, name, description, enabled, draft_version,"
            + "  draft_definition, draft_definition_sha256, created_by)"
            + " VALUES (@tenantId, @slug, @name, @description, true, 1, @canonicalDefinition::jsonb, @definitionSha256, @createdBy)"
            + " ON CONFLICT (tenant_id, slug) DO NOTHING"
            + $" RETURNING {AgentCols}",
            new { tenantId, slug, name, description, canonicalDefinition, definitionSha256, createdBy },
            cancellationToken: ct));
        return WithCanonicalDefinition(created);
    }

    public async Task<AgentDraftResult> UpdateDraftAsync(
        string tenantId, Guid id, long expectedVersion, string name, string description,
        string canonicalDefinition, string definitionSha256, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var updated = await conn.QuerySingleOrDefaultAsync<Agent>(new CommandDefinition(
            "UPDATE agent SET name = @name, description = @description,"
            + "  draft_definition = @canonicalDefinition::jsonb, draft_definition_sha256 = @definitionSha256,"
            + "  draft_version = draft_version + 1, draft_validated_version = NULL, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND id = @id AND draft_version = @expectedVersion"
            + $" RETURNING {AgentCols}",
            new { tenantId, id, expectedVersion, name, description, canonicalDefinition, definitionSha256 },
            cancellationToken: ct));

        if (updated is not null)
        {
            return new AgentDraftResult(AgentWriteStatus.Success, WithCanonicalDefinition(updated));
        }

        // 沒更新到:區分「不存在」與「版本不符(stale ETag)」。
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent WHERE tenant_id = @tenantId AND id = @id)",
            new { tenantId, id }, cancellationToken: ct));
        return new AgentDraftResult(
            exists ? AgentWriteStatus.VersionConflict : AgentWriteStatus.NotFound, null);
    }

    public async Task<bool> MarkValidatedAsync(
        string tenantId,
        Guid id,
        long version,
        string canonicalDefinition,
        string definitionSha256,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent SET draft_definition = CAST(@canonicalDefinition AS jsonb),"
            + " draft_definition_sha256 = @definitionSha256,"
            + " draft_validated_version = @version, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND id = @id AND draft_version = @version",
            new { tenantId, id, version, canonicalDefinition, definitionSha256 }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyList<AgentValidationError>> ValidateReferencesAsync(
        string tenantId, string canonicalDefinition, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var resolution = await ResolveReferencesAsync(
            conn, transaction: null, tenantId, canonicalDefinition, lockRows: false, ct);
        return resolution.Errors;
    }

    public async Task<AgentPublishResult> PublishAsync(
        string tenantId,
        Guid id,
        long expectedVersion,
        string canonicalDefinition,
        string definitionSha256,
        string createdBy,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var state = await conn.QuerySingleOrDefaultAsync<PublishState>(
            new CommandDefinition(
                "SELECT draft_version AS DraftVersion, draft_validated_version AS Validated,"
                + " published_revision AS Published"
                + " FROM agent WHERE tenant_id = @tenantId AND id = @id FOR UPDATE",
                new { tenantId, id }, tx, cancellationToken: ct));

        if (state is null)
        {
            await tx.RollbackAsync(ct);
            return new AgentPublishResult(AgentWriteStatus.NotFound, 0);
        }

        if (state.DraftVersion != expectedVersion || state.Validated != expectedVersion)
        {
            await tx.RollbackAsync(ct);
            return new AgentPublishResult(AgentWriteStatus.VersionConflict, 0);
        }

        // Canonicalization and validated-version consumption belong to this same locked transaction.
        // A concurrent publisher that arrives after commit sees validated=NULL and cannot revive/publish
        // the same ETag as another immutable revision.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent SET draft_definition = CAST(@canonicalDefinition AS jsonb),"
            + " draft_definition_sha256 = @definitionSha256, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id, canonicalDefinition, definitionSha256 },
            tx,
            cancellationToken: ct));

        // Reference resolution 與 pin 都在同一交易，並對 workflow/skill/current revision 加 SHARE lock；
        // Skill disable/update 無法在檢查與 INSERT 之間穿越。
        var resolution = await ResolveReferencesAsync(
            conn, tx, tenantId, canonicalDefinition, lockRows: true, ct);
        if (resolution.Errors.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return new AgentPublishResult(
                AgentWriteStatus.InvalidReference, 0, resolution.Errors);
        }

        var newRevision = (state.Published ?? 0) + 1;
        await SupersedeAsync(conn, tx, id, ct);

        // 由 draft_definition jsonb 抽欄落 revision;runtime_workflow.id/revision 從巢狀取出並轉型。
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO agent_revision (agent_id, revision, status, system_prompt, execution_roles,"
            + "  capabilities, output_contract, audience, business_rules, allowed_tools, knowledge_sources,"
            + "  runtime_limits, runtime_workflow_id, runtime_workflow_revision, definition_sha256, created_by)"
            + " SELECT id, @newRevision, 'published',"
            + "  draft_definition->>'system_prompt', draft_definition->'execution_roles',"
            + "  draft_definition->'capabilities', draft_definition->'output_contract',"
            + "  draft_definition->'audience', draft_definition->'business_rules',"
            + "  draft_definition->'allowed_tools', draft_definition->'knowledge_sources',"
            + "  draft_definition->'runtime_limits',"
            + "  NULLIF(draft_definition->'runtime_workflow'->>'id','')::uuid,"
            + "  NULLIF(draft_definition->'runtime_workflow'->>'revision','')::int,"
            + "  draft_definition_sha256, @createdBy"
            + " FROM agent WHERE id = @id",
            new { id, newRevision, createdBy }, tx, cancellationToken: ct));

        await InsertBindingsAsync(conn, tx, id, newRevision, resolution.Bindings, ct);

        // 清 draft_validated_version:要求再驗證才能再發,避免同一 draft 重複 publish 產生重複 revision。
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent SET published_revision = @newRevision, draft_validated_version = NULL, updated_at = now()"
            + " WHERE id = @id",
            new { id, newRevision }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new AgentPublishResult(AgentWriteStatus.Success, newRevision);
    }

    public async Task<IReadOnlyList<AgentRevisionInfo>> ListRevisionsAsync(
        string tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var revisions = (await conn.QueryAsync<RevisionRow>(new CommandDefinition(
            "SELECT r.revision AS Revision, r.status AS Status, r.definition_sha256 AS DefinitionSha256,"
            + " r.runtime_workflow_id AS RuntimeWorkflowId, r.runtime_workflow_revision AS RuntimeWorkflowRevision,"
            + " r.created_by AS CreatedBy, r.created_at AS CreatedAt"
            + " FROM agent_revision r JOIN agent a ON a.id = r.agent_id"
            + " WHERE a.tenant_id = @tenantId AND a.id = @id ORDER BY r.revision DESC",
            new { tenantId, id }, cancellationToken: ct))).AsList();

        var bindings = (await conn.QueryAsync<BindingRow>(new CommandDefinition(
            "SELECT rs.agent_revision AS AgentRevision, s.name AS Skill, rs.skill_revision AS SkillRevision,"
            + " rs.position AS Position, rs.enabled AS Enabled"
            + " FROM agent_revision_skill rs JOIN skill s ON s.id = rs.skill_id"
            + " JOIN agent a ON a.id = rs.agent_id"
            + " WHERE a.tenant_id = @tenantId AND a.id = @id",
            new { tenantId, id }, cancellationToken: ct))).AsList();

        var byRevision = bindings
            .GroupBy(b => b.AgentRevision)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AgentRevisionSkillInfo>)g.OrderBy(b => b.Position)
                    .Select(b => new AgentRevisionSkillInfo(b.Skill, b.SkillRevision, b.Position, b.Enabled))
                    .ToList());

        return revisions.Select(r => new AgentRevisionInfo(
            r.Revision, r.Status, r.DefinitionSha256, r.RuntimeWorkflowId, r.RuntimeWorkflowRevision,
            byRevision.GetValueOrDefault(r.Revision, Array.Empty<AgentRevisionSkillInfo>()),
            r.CreatedBy, r.CreatedAt)).ToList();
    }

    public async Task<string?> GetRevisionDefinitionAsync(
        string tenantId, Guid id, int revision, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RestoreDefinitionRow>(new CommandDefinition(
            "SELECT r.system_prompt AS SystemPrompt, r.execution_roles::text AS ExecutionRoles,"
            + " r.capabilities::text AS Capabilities, r.output_contract::text AS OutputContract,"
            + " r.audience::text AS Audience, r.business_rules::text AS BusinessRules,"
            + " r.allowed_tools::text AS AllowedTools, r.knowledge_sources::text AS KnowledgeSources,"
            + " r.runtime_limits::text AS RuntimeLimits, r.runtime_workflow_id AS RuntimeWorkflowId,"
            + " r.runtime_workflow_revision AS RuntimeWorkflowRevision"
            + " FROM agent_revision r JOIN agent a ON a.id = r.agent_id"
            + " WHERE a.tenant_id = @tenantId AND r.agent_id = @id AND r.revision = @revision",
            new { tenantId, id, revision }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var bindings = (await conn.QueryAsync<RestoreBindingRow>(new CommandDefinition(
            "SELECT s.name AS Skill, rs.position AS Position"
            + " FROM agent_revision_skill rs JOIN skill s ON s.id = rs.skill_id"
            + " JOIN agent a ON a.id = rs.agent_id"
            + " WHERE a.tenant_id = @tenantId AND rs.agent_id = @id AND rs.agent_revision = @revision"
            + " ORDER BY rs.position",
            new { tenantId, id, revision }, cancellationToken: ct))).AsList();

        var bindingArray = new JsonArray();
        foreach (var binding in bindings)
        {
            bindingArray.Add(new JsonObject
            {
                ["skill"] = binding.Skill,
                ["revision_policy"] = "latest",
            });
        }

        var definition = new JsonObject
        {
            ["system_prompt"] = row.SystemPrompt,
            ["execution_roles"] = JsonNode.Parse(row.ExecutionRoles),
            ["capabilities"] = JsonNode.Parse(row.Capabilities),
            ["output_contract"] = JsonNode.Parse(row.OutputContract),
            ["audience"] = JsonNode.Parse(row.Audience),
            ["allowed_tools"] = JsonNode.Parse(row.AllowedTools),
            ["skill_bindings"] = bindingArray,
            ["knowledge_sources"] = JsonNode.Parse(row.KnowledgeSources),
            ["business_rules"] = JsonNode.Parse(row.BusinessRules),
            ["runtime_limits"] = JsonNode.Parse(row.RuntimeLimits),
            ["runtime_workflow"] = new JsonObject
            {
                ["id"] = row.RuntimeWorkflowId?.ToString() ?? string.Empty,
                ["revision"] = row.RuntimeWorkflowRevision ?? 0,
            },
        };
        return AgentCanonicalizer.CanonicalizeDefinition(definition.ToJsonString());
    }

    public async Task<AgentPublishResult> RestoreAsync(
        string tenantId,
        Guid id,
        int revision,
        string canonicalDefinition,
        string definitionSha256,
        string createdBy,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var published = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT published_revision FROM agent WHERE tenant_id = @tenantId AND id = @id FOR UPDATE",
            new { tenantId, id }, tx, cancellationToken: ct));

        // QuerySingleOrDefault 對「無列」回 default(int?)=null,對「有列但欄位 NULL」也回 null → 需另判存在性。
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent WHERE tenant_id = @tenantId AND id = @id)",
            new { tenantId, id }, tx, cancellationToken: ct));
        if (!exists)
        {
            await tx.RollbackAsync(ct);
            return new AgentPublishResult(AgentWriteStatus.NotFound, 0);
        }

        var targetExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_revision WHERE agent_id = @id AND revision = @revision)",
            new { id, revision }, tx, cancellationToken: ct));
        if (!targetExists)
        {
            await tx.RollbackAsync(ct);
            return new AgentPublishResult(AgentWriteStatus.NotFound, 0);
        }

        var newRevision = (published ?? 0) + 1;
        await SupersedeAsync(conn, tx, id, ct);

        // 舊 revision 本身不可變；新 revision 使用 Workflow 本次重新驗證/正規化後的 definition。
        // pinned bindings 仍在下方從 target 原封複製，不重新解析 latest。
        await conn.ExecuteAsync(new CommandDefinition(
            "WITH normalized AS (SELECT CAST(@canonicalDefinition AS jsonb) AS definition)"
            + " INSERT INTO agent_revision (agent_id, revision, status, system_prompt, execution_roles,"
            + "  capabilities, output_contract, audience, business_rules, allowed_tools, knowledge_sources,"
            + "  runtime_limits, runtime_workflow_id, runtime_workflow_revision, definition_sha256, created_by)"
            + " SELECT r.agent_id, @newRevision, 'published',"
            + "  n.definition->>'system_prompt', n.definition->'execution_roles',"
            + "  n.definition->'capabilities', n.definition->'output_contract',"
            + "  n.definition->'audience', n.definition->'business_rules',"
            + "  n.definition->'allowed_tools', n.definition->'knowledge_sources',"
            + "  n.definition->'runtime_limits',"
            + "  NULLIF(n.definition->'runtime_workflow'->>'id','')::uuid,"
            + "  NULLIF(n.definition->'runtime_workflow'->>'revision','')::int,"
            + "  @definitionSha256, @createdBy"
            + " FROM agent_revision r CROSS JOIN normalized n"
            + " WHERE r.agent_id = @id AND r.revision = @revision",
            new
            {
                id,
                revision,
                newRevision,
                canonicalDefinition,
                definitionSha256,
                createdBy,
            },
            tx,
            cancellationToken: ct));

        // 固定的 skill bindings 一併複製(維持舊 revision 的 pin)。
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO agent_revision_skill (agent_id, agent_revision, skill_id, skill_revision, position, enabled)"
            + " SELECT agent_id, @newRevision, skill_id, skill_revision, position, enabled"
            + " FROM agent_revision_skill WHERE agent_id = @id AND agent_revision = @revision",
            new { id, revision, newRevision }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent SET published_revision = @newRevision, updated_at = now() WHERE id = @id",
            new { id, newRevision }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new AgentPublishResult(AgentWriteStatus.Success, newRevision);
    }

    public async Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent SET enabled = @enabled, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id, enabled }, cancellationToken: ct));
        return rows > 0;
    }

    private static Task SupersedeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_revision SET status = 'superseded' WHERE agent_id = @id AND status = 'published'",
            new { id }, tx, cancellationToken: ct));

    private static Agent? WithCanonicalDefinition(Agent? agent)
        => agent is null
            ? null
            : agent with
            {
                DraftDefinition = AgentCanonicalizer.CanonicalizeDefinition(agent.DraftDefinition),
            };

    private static async Task InsertBindingsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, int revision,
        IReadOnlyList<ResolvedSkillBinding> bindings, CancellationToken ct)
    {
        foreach (var b in bindings)
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO agent_revision_skill (agent_id, agent_revision, skill_id, skill_revision, position, enabled)"
                + " VALUES (@id, @revision, @skillId, @skillRevision, @position, true)",
                new { id, revision, b.SkillId, b.SkillRevision, b.Position }, tx, cancellationToken: ct));
            if (rows != 1)
            {
                throw new InvalidOperationException($"Skill pin 寫入失敗：{b.SkillName}");
            }
        }
    }

    private static async Task<ReferenceResolution> ResolveReferencesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        string tenantId,
        string canonicalDefinition,
        bool lockRows,
        CancellationToken ct)
    {
        var errors = new List<AgentValidationError>();
        var resolved = new List<ResolvedSkillBinding>();
        var workflow = AgentCanonicalizer.WorkflowOf(canonicalDefinition);

        if (!Guid.TryParse(workflow.Id, out var workflowId) || workflow.Revision <= 0)
        {
            errors.Add(new AgentValidationError(
                "runtime_workflow", "runtime_workflow 必須引用合法的 published revision"));
        }
        else if (workflowId == Guid.Parse(AgentDefaults.RuntimeWorkflowId)
                 && workflow.Revision == AgentDefaults.LegacyRuntimeWorkflowRevision)
        {
            errors.Add(new AgentValidationError(
                "runtime_workflow",
                "Default Agent-Runtime Workflow rev1 是不可再綁定的 legacy revision；請使用 current revision"));
        }
        else
        {
            var workflowSql =
                "SELECT w.id FROM workflow w"
                + " JOIN workflow_revision wr ON wr.workflow_id = w.id AND wr.revision = @revision"
                + " WHERE w.id = @workflowId AND w.enabled AND w.kind = 'agent-runtime'"
                + " AND w.published_revision IS NOT NULL"
                + " AND (w.tenant_id = @tenantId OR w.tenant_id = @systemTenant)"
                + (lockRows ? " FOR SHARE OF w, wr" : "");
            var visibleWorkflow = await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                workflowSql,
                new
                {
                    workflowId,
                    revision = workflow.Revision,
                    tenantId,
                    systemTenant = AgentDefaults.SystemTenant,
                },
                transaction,
                cancellationToken: ct));
            if (visibleWorkflow is null)
            {
                errors.Add(new AgentValidationError(
                    "runtime_workflow",
                    "Workflow 不存在、未發布、已停用、kind 不相容或不屬於目前 tenant/system"));
            }
        }

        var bindings = AgentCanonicalizer.SkillBindingsOf(canonicalDefinition);
        for (var position = 0; position < bindings.Count; position++)
        {
            var name = bindings[position].Skill!;
            var skillSql =
                "SELECT s.id AS Id, s.current_revision AS CurrentRevision"
                + " FROM skill s"
                + " JOIN skill_revision sr ON sr.skill_id = s.id AND sr.revision = s.current_revision"
                + " WHERE s.tenant_id = @tenantId AND s.name = @name AND s.enabled"
                + (lockRows ? " FOR SHARE OF s, sr" : "");
            var skill = await conn.QuerySingleOrDefaultAsync<SkillPinRow>(new CommandDefinition(
                skillSql, new { tenantId, name }, transaction, cancellationToken: ct));
            if (skill is null)
            {
                errors.Add(new AgentValidationError(
                    "skill_bindings",
                    $"Skill「{name}」沒有同 tenant、enabled、可固定的 persisted revision；"
                    + "builtin/catalog-only Skill 在 D1 不可綁定"));
                continue;
            }

            resolved.Add(new ResolvedSkillBinding(
                name, skill.CurrentRevision, position, skill.Id));
        }

        return new ReferenceResolution(errors, resolved);
    }

    private sealed record RevisionRow(
        int Revision, string Status, string DefinitionSha256,
        Guid? RuntimeWorkflowId, int? RuntimeWorkflowRevision, string CreatedBy, DateTime CreatedAt);

    private sealed record BindingRow(
        int AgentRevision, string Skill, int SkillRevision, int Position, bool Enabled);

    private sealed record RestoreDefinitionRow(
        string SystemPrompt,
        string ExecutionRoles,
        string Capabilities,
        string OutputContract,
        string Audience,
        string BusinessRules,
        string AllowedTools,
        string KnowledgeSources,
        string RuntimeLimits,
        Guid? RuntimeWorkflowId,
        int? RuntimeWorkflowRevision);

    private sealed record RestoreBindingRow(string Skill, int Position);

    private sealed record PublishState(
        long DraftVersion,
        long? Validated,
        int? Published);

    private sealed record SkillPinRow(Guid Id, int CurrentRevision);

    private sealed record ReferenceResolution(
        IReadOnlyList<AgentValidationError> Errors,
        IReadOnlyList<ResolvedSkillBinding> Bindings);
}
