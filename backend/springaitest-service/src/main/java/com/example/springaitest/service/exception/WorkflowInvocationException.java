package com.example.springaitest.service.exception;

/**
 * 呼叫工作流服務失敗（例如連線異常、工作流執行本身出錯等下游問題）。
 */
public class WorkflowInvocationException extends RuntimeException {

    public WorkflowInvocationException(String message, Throwable cause) {
        super(message, cause);
    }
}
