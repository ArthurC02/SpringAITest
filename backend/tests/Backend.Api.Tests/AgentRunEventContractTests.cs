using Backend.Api.AgentRuns;
using Backend.Api.Data.InMemory;
using System.Reflection;

namespace Backend.Api.Tests;

public sealed class AgentRunEventContractTests
{
    [Theory]
    [InlineData(typeof(AgentRunRepository))]
    [InlineData(typeof(InMemoryAgentRunRepository))]
    public void CancelAuditEvents_UseWorkflowCompleted(Type repositoryType)
    {
        var eventTypes = Assert.IsAssignableFrom<IReadOnlySet<string>>(
            repositoryType.GetField(
                "CancelAuditEventTypes",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));

        Assert.Contains("workflow_completed", eventTypes);
        Assert.DoesNotContain("legacy_flow_completed", eventTypes);
    }
}
