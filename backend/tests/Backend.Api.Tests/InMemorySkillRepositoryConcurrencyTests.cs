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
}
