package com.example.springaitest.service.dto;

import jakarta.validation.constraints.NotNull;

import java.util.Map;

/**
 * 業務層輸入 DTO：呼叫工作流時的請求內容。
 * {@code input} 為透傳給工作流服務的初始狀態（key-value 任意結構），
 * 實際格式由各工作流自行定義，此處不做進一步限制。
 */
public record WorkflowInvokeRequest(

        @NotNull(message = "input 不可為空")
        Map<String, Object> input
) {
}
