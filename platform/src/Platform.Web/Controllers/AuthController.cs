using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>認證端點,全部公開(免 token)。token 由 backend 簽發,platform 只轉發。</summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[ServiceFilter(typeof(AuthRateLimitFilter), Order = -3000)]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth) => _auth = auth;

    /// <summary>註冊 — 201 Created,回 AuthResult(role 一律 USER)。</summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await _auth.RegisterAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>登入 — 200 OK,回 LoginResult(含 backend 簽發的 token)。</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, ct);
        return Ok(result);
    }
}
