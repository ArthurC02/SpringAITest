package com.example.springaitest.service.exception;

/**
 * 呼叫工作流時輸入不符合下游 schema（工作流服務回傳 422）。
 */
public class WorkflowBadInputException extends RuntimeException {

    public WorkflowBadInputException(String message) {
        super(message);
    }
}
