using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Auth;

/// <summary>認證端點。JWT 由 backend 簽發(platform 只驗不簽)。</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly JwtService _jwt;

    public AuthController(AuthService auth, JwtService jwt)
    {
        _auth = auth;
        _jwt = jwt;
    }

    /// <summary>註冊 — 201 Created,回 AuthResult(role 一律 USER)。</summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await _auth.RegisterAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>登入 — 200 OK,回 LoginResponse(含 backend 簽發的 token)。</summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, ct);
        var token = _jwt.Issue(result.Username, result.Role, result.TenantCode, result.Capabilities);
        return Ok(new LoginResponse(token, result.Username, result.Role, result.TenantCode));
    }
}
