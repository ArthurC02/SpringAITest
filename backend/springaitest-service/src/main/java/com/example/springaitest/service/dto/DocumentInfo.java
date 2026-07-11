package com.example.springaitest.service.dto;

import com.fasterxml.jackson.annotation.JsonProperty;

/**
 * 業務層輸出 DTO：文件清單中的單筆項目。
 * {@code chunkCount}、{@code createdAt} 對應下游 JSON 的 snake_case 欄位
 * chunk_count、created_at。
 */
public record DocumentInfo(
        String id,
        String title,
        @JsonProperty("chunk_count") int chunkCount,
        @JsonProperty("created_at") String createdAt
) {
}
