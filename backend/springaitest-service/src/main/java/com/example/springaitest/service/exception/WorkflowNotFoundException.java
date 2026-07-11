package com.example.springaitest.service.exception;

/**
 * 指定名稱的工作流不存在（工作流服務回傳 404）。
 */
public class WorkflowNotFoundException extends RuntimeException {

    public WorkflowNotFoundException(String message) {
        super(message);
    }
}
