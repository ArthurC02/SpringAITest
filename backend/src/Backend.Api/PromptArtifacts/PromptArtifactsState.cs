namespace Backend.Api.PromptArtifacts;

/// <summary>
/// P1 kill switch (<c>PROMPT_ARTIFACTS_ENABLED</c>, default off, fail closed). Off: the prompt
/// component/manifest routes 404 before auth (same posture as the other feature gates in
/// Program.cs) and Agent publish never reads <c>prompt_manifest_revision</c>, so the publish path
/// stays byte-for-byte the one that shipped before this feature. Turning it off never deletes or
/// rewrites already-pinned snapshots (plan 03 §7).
/// </summary>
public sealed record PromptArtifactsState(bool Enabled);
