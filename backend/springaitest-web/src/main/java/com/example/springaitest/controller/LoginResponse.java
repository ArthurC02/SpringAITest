package com.example.springaitest.controller;

/**
 * 展示層輸出 DTO：登入成功後的回應（含已簽發的 JWT）。
 * token 由 web 層的 JwtService 簽發，因此不屬於業務層的 AuthResult。
 */
public record LoginResponse(
        String token,
        String username,
        String role,
        String tenantCode
) {
}
