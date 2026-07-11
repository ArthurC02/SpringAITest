package com.example.springaitest.service.exception;

/**
 * 指定代碼的租戶不存在（例如註冊時 tenantCode 查無對應租戶）。
 */
public class TenantNotFoundException extends RuntimeException {

    public TenantNotFoundException(String message) {
        super(message);
    }
}
