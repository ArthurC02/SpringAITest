package com.example.springaitest.service.dto;

/**
 * 業務層輸出 DTO：註冊 / 登入成功後的使用者資訊。
 * 不含 token —— token 由 web 層以 JwtService 簽發。
 */
public record AuthResult(
        String username,
        String role,
        String tenantCode
) {
}
