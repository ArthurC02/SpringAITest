package com.example.springaitest.service.dto;

import java.util.Map;

/**
 * 業務層輸出 DTO：單次工作流呼叫的結果。
 */
public record WorkflowInvokeResponse(
        String workflow,
        Map<String, Object> output
) {
}
