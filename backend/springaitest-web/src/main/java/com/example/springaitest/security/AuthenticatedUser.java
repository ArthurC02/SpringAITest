package com.example.springaitest.security;

/**
 * 通過 JWT 驗證後的使用者主體（Spring Security {@code Authentication} 的 principal）。
 * 由 {@link JwtAuthFilter} 從 JWT claims 組出，供 Controller 以
 * {@code @AuthenticationPrincipal} 取用。
 */
public record AuthenticatedUser(
        String username,
        String role,
        String tenantCode
) {
}
