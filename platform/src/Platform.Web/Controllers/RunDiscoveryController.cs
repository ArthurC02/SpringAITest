using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// O2 "Unified Runs and Tasks center" (04-operations-trigger-plan.md §3). Gated by
/// <c>RUN_DISCOVERY_ENABLED</c> before authentication (see Program.cs) and, per §8's "tenant +
/// exact capability" rollout strategy, the exact <c>workflow.manage</c> capability D4/D5/D7's
/// admin surfaces already require. A transparent proxy like <see cref="RunApprovalController"/>'s
/// O3 queue: Backend owns every filter, the tenant/owner/ADMIN-gated visibility rule, and the
/// keyset cursor. The query string is forwarded verbatim — O2 has ten independent filter
/// dimensions (vs. O3's two), so re-typing each one here would only be a second place for a
/// backend param name to drift out of sync, never an extra check (Backend fully validates
/// kind/status/limit/cursor itself).
/// </summary>
[ApiController]
[Route("api/runs")]
[Authorize(Policy = "workflow.manage")]
public sealed class RunDiscoveryController(IRunDiscoveryService runs) : ProxyControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Write(await runs.ListAsync(Request.QueryString.Value ?? string.Empty, User.ToUserContext(), ct));
}
