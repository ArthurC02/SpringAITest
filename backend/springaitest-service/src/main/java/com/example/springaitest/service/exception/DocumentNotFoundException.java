package com.example.springaitest.service.exception;

/**
 * 指定 id 的文件不存在（文件服務回傳 404）。
 */
public class DocumentNotFoundException extends RuntimeException {

    public DocumentNotFoundException(String message) {
        super(message);
    }
}
