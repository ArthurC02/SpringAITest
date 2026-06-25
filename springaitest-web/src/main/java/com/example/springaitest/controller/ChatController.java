package com.example.springaitest.controller;

import com.example.springaitest.service.ChatService;
import com.example.springaitest.service.dto.ChatRequest;
import com.example.springaitest.service.dto.ChatResponse;
import jakarta.validation.Valid;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

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

    /** 送出一則訊息並取得 AI 回覆。 */
    @PostMapping
    public ChatResponse chat(@Valid @RequestBody ChatRequest request) {
        return chatService.chat(request.message());
    }

    /** 取得歷史對話紀錄。 */
    @GetMapping("/history")
    public List<ChatResponse> history() {
        return chatService.history();
    }
}
