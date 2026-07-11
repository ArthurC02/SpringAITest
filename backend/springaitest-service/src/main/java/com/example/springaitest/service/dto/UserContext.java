package com.example.springaitest.service.dto;

/**
 * 業務層內部 DTO：呼叫下游服務時所需的身分資訊，
 * 由展示層從已驗證的 JWT 主體組出後傳入。
 */
public record UserContext(
        String userId,
        String tenantCode,
        String role
) {
}
