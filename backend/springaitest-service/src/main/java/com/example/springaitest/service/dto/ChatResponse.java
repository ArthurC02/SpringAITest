package com.example.springaitest.service.dto;

import java.time.Instant;

/**
 * 業務層輸出 DTO：單次聊天的回覆結果。
 */
public record ChatResponse(
        Long id,
        String reply,
        Instant createdAt
) {
}
