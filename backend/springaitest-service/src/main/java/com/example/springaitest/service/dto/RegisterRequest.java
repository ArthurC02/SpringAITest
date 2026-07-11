package com.example.springaitest.service.dto;

import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Size;

/**
 * 業務層輸入 DTO：使用者註冊請求。
 */
public record RegisterRequest(

        @NotBlank(message = "username 不可為空")
        String username,

        @NotBlank(message = "password 不可為空")
        @Size(min = 8, message = "password 長度至少 8 碼")
        String password,

        @NotBlank(message = "tenantCode 不可為空")
        String tenantCode,

        @NotBlank(message = "inviteCode 不可為空")
        String inviteCode
) {
}
