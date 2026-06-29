package com.example.springaitest.exception;

import java.time.Instant;
import java.util.Map;

/**
 * 統一的 API 錯誤回傳格式。
 */
public record ApiError(
        Instant timestamp,
        int status,
        String message,
        Map<String, String> fieldErrors
) {
}
