package com.example.springaitest.service;

import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.dto.WorkflowInfo;
import com.example.springaitest.service.dto.WorkflowInvokeResponse;

import java.util.List;
import java.util.Map;

/**
 * 業務層介面：定義工作流相關的業務行為。
 * 實作透過 HTTP 觸發外部的 Python（LangGraph）工作流服務。
 */
public interface WorkflowService {

    /**
     * 觸發指定名稱的工作流並同步取得執行結果。
     *
     * @param name  工作流名稱
     * @param input 工作流的初始狀態（任意 key-value 結構）
     * @param ctx   呼叫者身分（租戶 / 使用者 / 角色），將轉為下游 header
     * @return 工作流名稱與最終輸出
     */
    WorkflowInvokeResponse invoke(String name, Map<String, Object> input, UserContext ctx);

    /**
     * 取得工作流服務目前註冊的所有工作流。
     *
     * @param ctx 呼叫者身分
     */
    List<WorkflowInfo> list(UserContext ctx);
}
