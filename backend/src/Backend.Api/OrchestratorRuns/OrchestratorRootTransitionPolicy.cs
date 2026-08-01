using System.Text.Json;
using Backend.Api.Common;

namespace Backend.Api.OrchestratorRuns;

internal static class OrchestratorRootTransitionPolicy
{
    public static bool IsValid(OrchestratorRootTransitionRequest request)
        => request.ExpectedStateVersion >= 1
           && request.LeaseGeneration >= 1
           && !string.IsNullOrWhiteSpace(request.ClaimToken)
           && request.ClaimToken.Length <= 256
           && request.ToStatus is "waiting_input" or "completed" or "failed" or "cancelled" or "timed_out"
           && JsonUtf8.IsNullOrWithinLimit(request.Result, 1024 * 1024)
           && HasValidEvents(request.Events)
           && (request.ToStatus != "waiting_input"
               || !string.IsNullOrWhiteSpace(request.CheckpointRef)
                  && request.CheckpointVersion is >= 1);

    private static bool HasValidEvents(IReadOnlyList<OrchestratorRootEventAppend>? events)
        => events is null
           || events.Count <= 200
              && events.All(eventItem =>
                  !string.IsNullOrWhiteSpace(eventItem.EventType)
                  && eventItem.EventType.Length <= 100
                  && !eventItem.EventType.Any(char.IsControl)
                  && JsonUtf8.IsNullOrWithinLimit(eventItem.Payload, 64 * 1024));
}
