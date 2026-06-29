package com.example.springaitest.service;

import com.example.springaitest.domain.entity.Conversation;
import com.example.springaitest.domain.repository.ConversationRepository;
import com.example.springaitest.service.dto.ChatResponse;
import com.example.springaitest.service.impl.ChatServiceImpl;
import io.micrometer.observation.ObservationRegistry;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.ArgumentCaptor;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;
import org.springframework.ai.chat.client.ChatClient;

import java.time.Instant;
import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

/**
 * 業務層單元測試：把 ChatClient（外部 LLM）與 Repository（資料庫）全部 mock 掉，
 * 只驗證 ChatServiceImpl 自身的協調邏輯。
 */
@ExtendWith(MockitoExtension.class)
class ChatServiceImplTest {

    @Mock
    private ChatClient.Builder builder;

    @Mock
    private ChatClient chatClient;

    @Mock
    private ChatClient.ChatClientRequestSpec requestSpec;

    @Mock
    private ChatClient.CallResponseSpec responseSpec;

    @Mock
    private ConversationRepository conversationRepository;

    private ChatServiceImpl chatService;

    @BeforeEach
    void setUp() {
        // 建構子內部會呼叫 builder.build()
        when(builder.build()).thenReturn(chatClient);
        chatService = new ChatServiceImpl(builder, conversationRepository, ObservationRegistry.NOOP, "gpt-4o-mini");
    }

    @Test
    void chat_shouldCallLlmAndPersistConversation() {
        // 模擬 ChatClient 流式 API：prompt().user(...).call().content()
        when(chatClient.prompt()).thenReturn(requestSpec);
        when(requestSpec.user("你好")).thenReturn(requestSpec);
        when(requestSpec.call()).thenReturn(responseSpec);
        when(responseSpec.content()).thenReturn("你好，我是 AI");
        when(conversationRepository.save(any(Conversation.class)))
                .thenAnswer(invocation -> invocation.getArgument(0));

        ChatResponse result = chatService.chat("你好");

        // 回傳的 reply 應為 LLM 的輸出
        assertThat(result.reply()).isEqualTo("你好，我是 AI");

        // 驗證存進資料庫的內容正確
        ArgumentCaptor<Conversation> captor = ArgumentCaptor.forClass(Conversation.class);
        verify(conversationRepository).save(captor.capture());
        Conversation saved = captor.getValue();
        assertThat(saved.getPrompt()).isEqualTo("你好");
        assertThat(saved.getReply()).isEqualTo("你好，我是 AI");
        assertThat(saved.getCreatedAt()).isNotNull();
    }

    @Test
    void history_shouldMapEntitiesToResponses() {
        Conversation c1 = new Conversation("問題1", "回覆1", Instant.now());
        Conversation c2 = new Conversation("問題2", "回覆2", Instant.now());
        when(conversationRepository.findAllByOrderByCreatedAtDesc())
                .thenReturn(List.of(c1, c2));

        List<ChatResponse> history = chatService.history();

        assertThat(history).hasSize(2);
        assertThat(history).extracting(ChatResponse::reply)
                .containsExactly("回覆1", "回覆2");
    }
}
