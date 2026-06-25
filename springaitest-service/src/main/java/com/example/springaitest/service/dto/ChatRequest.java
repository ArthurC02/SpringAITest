package com.example.springaitest.service.dto;

import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Size;

/**
 * 業務層輸入 DTO：使用者送出的聊天請求（同時是展示層與業務層之間的契約）。
 * 透過 Bean Validation 在進入業務層前先驗證。
 */
public record ChatRequest(

        @NotBlank(message = "message 不可為空")
        @Size(max = 4000, message = "message 長度不可超過 4000 字")
        String message
) {
}
