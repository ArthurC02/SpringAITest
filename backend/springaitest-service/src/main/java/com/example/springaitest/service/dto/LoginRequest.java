package com.example.springaitest.service.dto;

import jakarta.validation.constraints.NotBlank;

/**
 * 業務層輸入 DTO：使用者登入請求。
 */
public record LoginRequest(

        @NotBlank(message = "username 不可為空")
        String username,

        @NotBlank(message = "password 不可為空")
        String password
) {
}
