using System.Text.Json;
using Backend.Api.OrchestratorRuns;

namespace Backend.Api.Tests;

public sealed class OrchestratorRootTransitionPolicyTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    public void AllowedTerminalStatuses_AreAccepted(string status)
        => Assert.True(OrchestratorRootTransitionPolicy.IsValid(Request(status)));

    [Fact]
    public void WaitingInput_RequiresPinnedCheckpoint()
    {
        Assert.False(OrchestratorRootTransitionPolicy.IsValid(Request("waiting_input")));
        Assert.True(OrchestratorRootTransitionPolicy.IsValid(
            Request("waiting_input") with
            {
                CheckpointRef = "rctx1:checkpoint",
                CheckpointVersion = 1,
            }));
    }

    [Fact]
    public void Utf8ResultAndEventCaps_UseTheirInclusiveByteBoundaries()
    {
        var acceptedResult = EmojiJson(262_141);
        var rejectedResult = EmojiJson(262_142);
        Assert.True(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Result = acceptedResult }));
        Assert.False(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Result = rejectedResult }));

        var acceptedEvent = new OrchestratorRootEventAppend("trace", EmojiJson(16_381));
        var rejectedEvent = new OrchestratorRootEventAppend("trace", EmojiJson(16_382));
        Assert.True(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Events = [acceptedEvent] }));
        Assert.False(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Events = [rejectedEvent] }));
    }

    [Fact]
    public void Events_EnforceCountTypeLengthAndControlCharacters()
    {
        var eventItem = new OrchestratorRootEventAppend("trace", null);
        Assert.True(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Events = Enumerable.Repeat(eventItem, 200).ToArray() }));
        Assert.False(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Events = Enumerable.Repeat(eventItem, 201).ToArray() }));
        Assert.True(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Events = [new(new string('t', 100), null)] }));
        Assert.False(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Events = [new(new string('t', 101), null)] }));
        Assert.False(OrchestratorRootTransitionPolicy.IsValid(
            Request("completed") with { Events = [new("trace\n", null)] }));
    }

    private static OrchestratorRootTransitionRequest Request(string status)
        => new(1, "claim", 1, status);

    private static JsonElement EmojiJson(int count)
        => JsonDocument.Parse("{\"data\":\"" + string.Concat(Enumerable.Repeat("😀", count)) + "\"}")
            .RootElement.Clone();
}
