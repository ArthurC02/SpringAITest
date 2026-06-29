package com.example.springaitest.controller;

import com.example.springaitest.service.ChatService;
import com.example.springaitest.service.dto.ChatRequest;
import com.example.springaitest.service.dto.ChatResponse;
import jakarta.validation.Valid;
import org.springframework.http.MediaType;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;
import reactor.core.publisher.Flux;

import java.util.List;

/**
 * 展示層：對外的 REST API 進入點。
 * 只負責接收請求 / 回傳結果，業務邏輯一律委派給 Service。
 */
@RestController
@RequestMapping("/api/chat")
public class ChatController {

    private final ChatService chatService;

    public ChatController(ChatService chatService) {
        this.chatService = chatService;
    }

    /** 送出一則訊息並取得 AI 回覆（一次性，非串流）。 */
    @PostMapping
    public ChatResponse chat(@Valid @RequestBody ChatRequest request) {
        return chatService.chat(request.message());
    }

    /**
     * 送出一則訊息並以 SSE 串流逐字回傳 AI 回覆。
     * 回傳 {@code Flux<String>}：Spring MVC 會把每個元素包成一筆 SSE 事件（data:）推給前端，
     * 前端即可逐 token 顯示。串流結束後，完整回覆由 Service 落檔。
     */
    @PostMapping(value = "/stream", produces = MediaType.TEXT_EVENT_STREAM_VALUE)
    public Flux<String> stream(@Valid @RequestBody ChatRequest request) {
        return chatService.streamChat(request.message());
    }

    /** 取得歷史對話紀錄。 */
    @GetMapping("/history")
    public List<ChatResponse> history() {
        return chatService.history();
    }
}
