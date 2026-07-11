package com.example.springaitest.service.dto;

import com.fasterxml.jackson.annotation.JsonProperty;

/**
 * 業務層輸出 DTO：建立文件成功後的結果。
 * {@code chunkCount} 對應下游 JSON 的 snake_case 欄位 chunk_count。
 */
public record DocumentCreated(
        String id,
        String title,
        @JsonProperty("chunk_count") int chunkCount
) {
}
