package com.example.springaitest.controller;

import com.example.springaitest.security.JwtService;
import com.example.springaitest.service.ChatService;
import com.example.springaitest.service.dto.ChatResponse;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
import org.springframework.http.MediaType;
import org.springframework.test.context.bean.override.mockito.MockitoBean;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;
import reactor.core.publisher.Flux;

import java.nio.charset.StandardCharsets;
import java.time.Instant;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.asyncDispatch;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.request;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

/**
 * 展示層單元測試：只載入 Web 切片並 mock 掉 Service，
 * 驗證路由、請求驗證與回應序列化。
 * {@code addFilters = false}：/api/chat/** 在 SecurityConfig 中維持公開，但 @WebMvcTest
 * 不會載入 SecurityConfig，只會套用 Spring Boot 對 Security 的預設自動配置（要求逐一認證），
 * 因此關閉 filter 鏈以還原「公開路由」的行為。
 */
@WebMvcTest(ChatController.class)
@AutoConfigureMockMvc(addFilters = false)
class ChatControllerTest {

    @Autowired
    private MockMvc mockMvc;

    @MockitoBean
    private ChatService chatService;

    // JwtAuthFilter 是 Filter，@WebMvcTest 的切片會自動載入它，因而需要它依賴的 JwtService 也在容器中。
    @MockitoBean
    private JwtService jwtService;

    @Test
    void chat_shouldReturnReply() throws Exception {
        when(chatService.chat(eq("你好"), any(), any()))
                .thenReturn(new ChatResponse(1L, "你好，我是 AI", Instant.parse("2026-01-01T00:00:00Z")));

        mockMvc.perform(post("/api/chat")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"message\":\"你好\"}"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.id").value(1))
                .andExpect(jsonPath("$.reply").value("你好，我是 AI"));
    }

    @Test
    void stream_shouldReturnSseStreamOfReplyChunks() throws Exception {
        when(chatService.streamChat(eq("你好"), any(), any()))
                .thenReturn(Flux.just("你好", "，我是 AI"));

        // Flux 回傳型別走 Spring MVC 非同步流程：先確認 async 已啟動，再 dispatch 取完整結果。
        MvcResult result = mockMvc.perform(post("/api/chat/stream")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"message\":\"你好\"}"))
                .andExpect(request().asyncStarted())
                .andReturn();

        MvcResult dispatched = mockMvc.perform(asyncDispatch(result))
                .andExpect(status().isOk())
                .andExpect(content().contentTypeCompatibleWith(MediaType.TEXT_EVENT_STREAM))
                .andReturn();

        // SSE 回應的 content-type 不帶 charset，MockMvc 預設以 ISO-8859-1 解碼會讓中文變亂碼，
        // 因此直接取原始 bytes 以 UTF-8 還原，再驗證每個 chunk 都被包成 data: 事件送出。
        String body = dispatched.getResponse().getContentAsString(StandardCharsets.UTF_8);
        assertThat(body)
                .contains("data:你好")
                .contains("data:，我是 AI");
    }

    @Test
    void chat_shouldReturn400WhenMessageBlank() throws Exception {
        mockMvc.perform(post("/api/chat")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"message\":\"\"}"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.status").value(400))
                .andExpect(jsonPath("$.fieldErrors.message").exists());
    }
}
