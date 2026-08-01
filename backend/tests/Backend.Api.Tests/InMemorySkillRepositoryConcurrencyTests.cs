using System.Text;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

public sealed class InMemorySkillRepositoryConcurrencyTests
{
    private static Skill Value(string name, string definition, string kind = "flow") => new(
        name, definition, definition, "USER", true, 0, default, default, kind);

    [Fact]
    public async Task ConcurrentImportAndUpdate_ProduceUniqueContinuousRevisionsWithMatchingSnapshots()
    {
        var repo = new InMemorySkillRepository();
        const string tenant = "atomic-tenant";
        const string name = "atomic-skill";
        await repo.CreateAsync(
            tenant, Value(name, "name: atomic-skill\nversion: seed\n"), "seed", default);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 40).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var definition = $"name: {name}\nversion: {index}\n";
            if (index % 2 == 0)
            {
                var stored = await repo.UpdateAsync(
                    tenant, name, Value(name, definition), $"u-{index}", default);
                return (Index: index, Definition: definition, Kind: "flow",
                    Package: (byte[]?)null, Stored: stored!);
            }

            var package = Encoding.UTF8.GetBytes($"zip-{index}");
            var storedImport = await repo.ImportAsync(
                tenant, Value(name, definition, "agentic"), package,
                SkillHash.Sha256(package), $"u-{index}", default);
            return (Index: index, Definition: definition, Kind: "agentic",
                Package: package, Stored: storedImport!);
        })).ToArray();

        start.SetResult();
        var writes = await Task.WhenAll(tasks);

        Assert.Equal(40, writes.Select(w => w.Stored.CurrentRevision).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(2, 40),
            writes.Select(w => w.Stored.CurrentRevision).OrderBy(r => r));

        foreach (var write in writes)
        {
            var snapshot = await repo.GetRevisionAsync(
                tenant, name, write.Stored.CurrentRevision, default);
            Assert.NotNull(snapshot);
            Assert.Equal(write.Definition, snapshot!.Definition);
            Assert.Equal(write.Kind, snapshot.Kind);
            Assert.Equal(write.Package, snapshot.Package);
            Assert.Equal(SkillHash.Sha256(write.Definition), snapshot.DefinitionSha256);
        }

        var revisions = await repo.ListRevisionsAsync(tenant, name, default);
        Assert.Equal(41, revisions.Count);
        Assert.Equal(
            Enumerable.Range(1, 41).Reverse(),
            revisions.Select(r => r.Revision));
        var current = await repo.GetAsync(tenant, name, default);
        Assert.Equal(41, current!.CurrentRevision);
        Assert.Equal(
            current.Definition,
            (await repo.GetRevisionAsync(tenant, name, 41, default))!.Definition);
    }

    // CreateAsync 的另半邊:同名併發建立必須恰好一個成功(其餘回 null → controller 409)。
    // read-modify-write 沒有 gate 保護的話會出現兩個「贏家」+ 兩筆 revision 1 —— lite 模式沒有
    // DB 的 UNIQUE (tenant_id, name) / uq_skill_revision 兜底,這裡是唯一防線。
    [Fact]
    public async Task ConcurrentCreate_SameName_ExactlyOneWins()
    {
        var repo = new InMemorySkillRepository();
        const string tenant = "atomic-tenant";
        const string name = "atomic-create";

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await repo.CreateAsync(
                tenant, Value(name, $"name: {name}\nversion: {index}\n"), $"c-{index}", default);
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(tasks);

        var winner = Assert.Single(results, r => r is not null)!;
        Assert.Equal(1, winner.CurrentRevision);

        // 稽核鏈只有一筆,且內容屬於那個贏家(不是別人的 definition)。
        var revision = Assert.Single(await repo.ListRevisionsAsync(tenant, name, default));
        Assert.Equal(1, revision.Revision);
        Assert.Equal(winner.Definition, revision.Definition);
        Assert.Equal(winner.Definition, (await repo.GetAsync(tenant, name, default))!.Definition);
    }

    // DeleteAsync 的另半邊:同一列的併發軟刪必須恰好一個成功(其餘回 false → controller 404)。
    // read-check-write 少了 lock 或 enabled 檢查,16 個呼叫會全部回 true,DELETE 的冪等語義就沒了。
    [Fact]
    public async Task ConcurrentDelete_SameSkill_ExactlyOneWins()
    {
        var repo = new InMemorySkillRepository();
        const string tenant = "atomic-tenant";
        const string name = "atomic-delete";
        await repo.CreateAsync(
            tenant, Value(name, "name: atomic-delete\nversion: 1\n"), "seed", default);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await repo.DeleteAsync(tenant, name, default);
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, deleted => deleted);

        // 軟刪後不可見,但稽核鏈不動:revision 停在 1 且仍查得到。
        Assert.Null(await repo.GetAsync(tenant, name, default));
        var revision = Assert.Single(await repo.ListRevisionsAsync(tenant, name, default));
        Assert.Equal(1, revision.Revision);
    }

    [Fact]
    public async Task CompatibilityDelete_GetThenImportBeforeWrite_IsStoppedByFlowFence()
    {
        var repo = new InMemorySkillRepository();
        const string tenant = "atomic-tenant";
        const string name = "compat-delete-race";
        await repo.CreateAsync(tenant, Value(name, "name: flow-v1"), "seed", default);

        var readObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compatibilityDelete = Task.Run(async () =>
        {
            var before = await repo.GetAsync(tenant, name, default);
            Assert.Equal("flow", before!.Kind);
            readObserved.SetResult();
            await importFinished.Task;
            return await repo.DeleteAsync(tenant, name, "flow", default);
        });
        var import = Task.Run(async () =>
        {
            await readObserved.Task;
            var package = Encoding.UTF8.GetBytes("agent-delete-race-package");
            var result = await repo.ImportAsync(
                tenant, Value(name, "kind: agentic", "agentic"), package,
                SkillHash.Sha256(package), "agent-writer", default);
            importFinished.SetResult();
            return result;
        });

        Assert.False(await compatibilityDelete);
        Assert.Equal(2, (await import)!.CurrentRevision);

        // flow-scoped 軟刪不得停用已被轉成 Agent Skill 的列,而且軟刪本來就不寫稽核鏈。
        var current = await repo.GetAsync(tenant, name, default);
        Assert.Equal("agentic", current!.Kind);
        Assert.Equal("kind: agentic", current.Definition);
        Assert.Equal(2, current.CurrentRevision);
        var revisions = await repo.ListRevisionsAsync(tenant, name, default);
        Assert.Equal(new[] { 2, 1 }, revisions.Select(r => r.Revision));
    }

    [Fact]
    public async Task ExpectedKindUpdate_RacingAgenticImport_CannotOverwriteConvertedRow()
    {
        var repo = new InMemorySkillRepository();
        const string tenant = "atomic-tenant";
        const string name = "kind-fenced-update";
        await repo.CreateAsync(tenant, Value(name, "name: seed"), "seed", default);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = Task.Run(async () =>
        {
            await start.Task;
            return await repo.UpdateAsync(
                tenant, name, "flow", Value(name, "name: flow-update"), "flow-writer", default);
        });
        var import = Task.Run(async () =>
        {
            await start.Task;
            var package = Encoding.UTF8.GetBytes("agent-package");
            return await repo.ImportAsync(
                tenant, Value(name, "name: agent-update", "agentic"), package,
                SkillHash.Sha256(package), "agent-writer", default);
        });

        start.SetResult();
        var writes = await Task.WhenAll(update, import);
        var updateResult = writes[0];
        var importResult = writes[1]!;

        var current = await repo.GetAsync(tenant, name, default);
        Assert.Equal("agentic", current!.Kind);
        Assert.Equal("name: agent-update", current.Definition);
        if (updateResult is not null)
        {
            Assert.True(updateResult.CurrentRevision < importResult.CurrentRevision);
        }
    }

    [Fact]
    public async Task FlowOnlyRevive_RacingAgenticImport_CannotReplaceDisabledAgentSkill()
    {
        var repo = new InMemorySkillRepository();
        const string tenant = "atomic-tenant";
        const string name = "kind-fenced-revive";
        var originalPackage = Encoding.UTF8.GetBytes("original-agent-package");
        await repo.ImportAsync(
            tenant, Value(name, "kind: agentic", "agentic"), originalPackage,
            SkillHash.Sha256(originalPackage), "seed", default);
        Assert.True(await repo.DeleteAsync(tenant, name, default));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revive = Task.Run(async () =>
        {
            await start.Task;
            return await repo.CreateAsync(
                tenant, "flow", Value(name, "name: forbidden-flow"), "flow-writer", default);
        });
        var import = Task.Run(async () =>
        {
            await start.Task;
            var package = Encoding.UTF8.GetBytes("replacement-agent-package");
            return await repo.ImportAsync(
                tenant, Value(name, "kind: agentic\nversion: 2", "agentic"), package,
                SkillHash.Sha256(package), "agent-writer", default);
        });

        start.SetResult();
        var writes = await Task.WhenAll(revive, import);

        Assert.Null(writes[0]);
        var current = await repo.GetAsync(tenant, name, default);
        Assert.Equal("agentic", current!.Kind);
        Assert.Equal("kind: agentic\nversion: 2", current.Definition);
        Assert.Equal(2, current.CurrentRevision);
    }

    [Fact]
    public async Task CompatibilityUpdate_GetThenImportBeforeWrite_IsStoppedByFlowFence()
    {
        var repo = new InMemorySkillRepository();
        const string tenant = "atomic-tenant";
        const string name = "compat-update-race";
        await repo.CreateAsync(tenant, Value(name, "name: flow-v1"), "seed", default);

        var readObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compatibilityUpdate = Task.Run(async () =>
        {
            var before = await repo.GetAsync(tenant, name, default);
            Assert.Equal("flow", before!.Kind);
            readObserved.SetResult();
            await importFinished.Task;
            return await repo.UpdateAsync(
                tenant, name, "flow", Value(name, "name: stale-flow-write"), "flow-writer", default);
        });
        var import = Task.Run(async () =>
        {
            await readObserved.Task;
            var package = Encoding.UTF8.GetBytes("agent-race-package");
            var result = await repo.ImportAsync(
                tenant, Value(name, "kind: agentic", "agentic"), package,
                SkillHash.Sha256(package), "agent-writer", default);
            importFinished.SetResult();
            return result;
        });

        var writes = await Task.WhenAll(compatibilityUpdate, import);

        Assert.Null(writes[0]);
        var current = await repo.GetAsync(tenant, name, default);
        Assert.Equal("agentic", current!.Kind);
        Assert.Equal("kind: agentic", current.Definition);
        Assert.Equal(2, current.CurrentRevision);
    }
}
