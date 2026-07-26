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
}
