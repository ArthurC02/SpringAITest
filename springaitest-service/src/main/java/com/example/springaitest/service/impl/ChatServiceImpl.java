package com.example.springaitest.service.impl;

import com.example.springaitest.domain.entity.Conversation;
import com.example.springaitest.domain.repository.ConversationRepository;
import com.example.springaitest.service.ChatService;
import com.example.springaitest.service.dto.ChatResponse;
import io.micrometer.observation.Observation;
import io.micrometer.observation.ObservationRegistry;
import org.springframework.ai.chat.client.ChatClient;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.time.Instant;
import java.util.List;

/**
 * 業務層實作：
 * 1. 透過 Spring AI 的 ChatClient 呼叫 OpenAI 取得回覆。
 * 2. 將「輸入 + 回覆」存入資料層。
 *
 * 此層只負責協調，不直接接觸 HTTP（交給 Controller）或 SQL（交給 Repository）。
 */
@Service
public class ChatServiceImpl implements ChatService {

    private final ChatClient chatClient;
    private final ConversationRepository conversationRepository;
    private final ObservationRegistry observationRegistry;
    private final String model;

    public ChatServiceImpl(ChatClient.Builder chatClientBuilder,
                           ConversationRepository conversationRepository,
                           ObservationRegistry observationRegistry,
                           @Value("${spring.ai.openai.chat.options.model:unknown}") String model) {
        this.chatClient = chatClientBuilder.build();
        this.conversationRepository = conversationRepository;
        this.observationRegistry = observationRegistry;
        this.model = model;
    }

    @Override
    @Transactional
    public ChatResponse chat(String message) {
        // 自訂業務 span：包住「呼叫 LLM + 存檔」。span 開始後仍可補標籤，
        // 所以回覆字數在取得回覆後才加上。observe(...) 期間，Spring AI 的
        // ChatClient span 會成為這個 span 的子節點，於 Langfuse 形成巢狀結構。
        Observation observation = Observation.createNotStarted("chat.service", observationRegistry)
                .contextualName("chat-service")
                .lowCardinalityKeyValue("gen_ai.operation.name", "chat")
                .lowCardinalityKeyValue("gen_ai.system", "openai")
                .lowCardinalityKeyValue("gen_ai.request.model", model)
                .highCardinalityKeyValue("prompt.length", String.valueOf(message.length()));

        return observation.observe(() -> {
            String reply = chatClient.prompt()
                    .user(message)
                    .call()
                    .content();

            observation.highCardinalityKeyValue("completion.length",
                    String.valueOf(reply == null ? 0 : reply.length()));

            Conversation saved = conversationRepository.save(
                    new Conversation(message, reply, Instant.now()));

            return toResponse(saved);
        });
    }

    @Override
    @Transactional(readOnly = true)
    public List<ChatResponse> history() {
        return conversationRepository.findAllByOrderByCreatedAtDesc().stream()
                .map(this::toResponse)
                .toList();
    }

    private ChatResponse toResponse(Conversation conversation) {
        return new ChatResponse(
                conversation.getId(),
                conversation.getReply(),
                conversation.getCreatedAt());
    }
}
