package com.example.springaitest.service.exception;

/**
 * 呼叫工作流時角色權限不足（工作流服務回傳 403）。
 */
public class WorkflowForbiddenException extends RuntimeException {

    public WorkflowForbiddenException(String message) {
        super(message);
    }
}
