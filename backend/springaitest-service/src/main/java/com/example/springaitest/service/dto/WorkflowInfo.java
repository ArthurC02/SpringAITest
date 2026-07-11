package com.example.springaitest.service.dto;

import com.fasterxml.jackson.annotation.JsonProperty;

/**
 * 業務層輸出 DTO：工作流服務所提供的單一工作流描述。
 * {@code requiredRole} 對應下游 JSON 的 snake_case 欄位 required_role。
 */
public record WorkflowInfo(
        String name,
        String description,
        @JsonProperty("required_role") String requiredRole
) {
}
