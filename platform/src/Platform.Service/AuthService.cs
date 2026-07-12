using System.Net.Http.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 認證服務:轉呼叫 backend /api/auth/*。backend 的 ApiError 依狀態碼映射回既有例外
/// (404→TenantNotFound、403→InvalidInviteCode、409→UsernameTaken、401→InvalidCredentials),
/// 訊息一律沿用 backend 回傳的 message,對外行為與本地實作時完全一致。
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly BackendClient _backend;

    public AuthService(BackendClient backend) => _backend = backend;

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Post, "/api/auth/register", body: new
        {
            username = request.Username,
            password = request.Password,
            tenantCode = request.TenantCode,
            inviteCode = request.InviteCode,
        });
        using var resp = await _backend.SendAsync(
            req, ex => new BackendCallException("認證服務呼叫失敗：" + ex.Message, ex), ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        return await resp.Content.ReadFromJsonAsync<AuthResult>(_backend.Json, ct)
            ?? throw new BackendCallException("認證服務呼叫失敗：回應內容為空");
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Post, "/api/auth/login", body: new
        {
            username = request.Username,
            password = request.Password,
        });
        using var resp = await _backend.SendAsync(
            req, ex => new BackendCallException("認證服務呼叫失敗：" + ex.Message, ex), ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        return await resp.Content.ReadFromJsonAsync<LoginResult>(_backend.Json, ct)
            ?? throw new BackendCallException("認證服務呼叫失敗：回應內容為空");
    }

    /// <summary>backend 錯誤狀態碼 → 既有例外;訊息用 backend 的 message。非預期狀態碼 → 500。</summary>
    private async Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var message = await _backend.ReadErrorMessageAsync(resp, ct);
        return (int)resp.StatusCode switch
        {
            404 => new TenantNotFoundException(message),
            403 => new InvalidInviteCodeException(message),
            409 => new UsernameTakenException(message),
            401 => new InvalidCredentialsException(message),
            _ => new BackendCallException("認證服務呼叫失敗：HTTP " + (int)resp.StatusCode),
        };
    }
}
