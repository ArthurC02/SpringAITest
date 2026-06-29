package com.example.springaitest.service;

import com.example.springaitest.service.dto.ChatResponse;
import reactor.core.publisher.Flux;

import java.util.List;

/**
 * 業務層介面：定義聊天相關的業務行為。
 * 以介面與實作分離，方便替換實作與單元測試。
 */
public interface ChatService {

    /**
     * 將使用者訊息送至 LLM，取得回覆並保存對話紀錄。
     *
     * @param message 使用者輸入
     * @return 含回覆內容與紀錄編號的結果
     */
    ChatResponse chat(String message);

    /**
     * 以串流方式將使用者訊息送至 LLM，逐塊回傳回覆內容（token chunk）。
     * 串流結束時，會把累積的完整回覆保存為一筆對話紀錄。
     *
     * @param message 使用者輸入
     * @return 回覆內容片段的 Flux（由 Controller 包成 SSE 推給前端）
     */
    Flux<String> streamChat(String message);

    /**
     * 取得所有歷史對話（由新到舊）。
     */
    List<ChatResponse> history();
}
