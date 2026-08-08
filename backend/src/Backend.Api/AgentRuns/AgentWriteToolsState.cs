namespace Backend.Api.AgentRuns;

/// <summary>
/// D7 kill switch (default off, fail closed), resolved once at startup like the other rollout
/// flags. The authoritative gate is the Program.cs middleware, which 404s every D7 route before
/// the internal-token check; this DI state exists only so the controller keeps its own
/// defense-in-depth check without reading <c>IConfiguration</c> at request time.
/// </summary>
public sealed record AgentWriteToolsState(bool Enabled);
