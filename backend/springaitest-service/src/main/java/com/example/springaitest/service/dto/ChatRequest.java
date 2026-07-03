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
        String message,

        // 長期記憶（mem0）分群用的使用者識別（前端帶入）。可省略；缺值時業務層歸為 default 使用者。
        @Size(max = 128, message = "userId 長度不可超過 128 字")
        String userId,

        // 短期記憶（同一對話多輪脈絡）分群用的對話識別（前端帶入，清除對話時會換新）。可省略。
        @Size(max = 128, message = "conversationId 長度不可超過 128 字")
        String conversationId
) {
}
