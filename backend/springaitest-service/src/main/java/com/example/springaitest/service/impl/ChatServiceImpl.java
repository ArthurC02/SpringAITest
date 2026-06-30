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
import reactor.core.publisher.Flux;

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
        Observation observation = newChatObservation(message);

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
    public Flux<String> streamChat(String message) {
        // 串流版本：用 ChatClient.stream() 取得逐塊回覆。
        // 注意：這裡「不」加 @Transactional —— 方法回傳的是 Flux，真正的資料流在「被訂閱時」
        //   才發生，而 @Transactional 只會包住「組裝 Flux」這一瞬間，無法涵蓋整段串流。
        //   因此改為在串流結束（doOnComplete）時，呼叫 repository.save() 落檔；
        //   Spring Data 的每個 repository 方法本身即為一個獨立交易，足以保證該筆寫入的原子性。
        //
        // 觀測同理：chat() 用的 observe(...) 會「進入 lambda 前開 scope、離開即關」，只適用於阻塞流程，
        //   無法涵蓋非同步的串流生命週期。因此改為手動 start()，並在串流真正結束的 doFinally 才 stop()；
        //   串流期間於 doOnComplete / doOnError 補上回覆字數與錯誤標籤。
        Observation observation = newChatObservation(message);
        observation.start();

        StringBuilder full = new StringBuilder();
        return chatClient.prompt()
                .user(message)
                .stream()
                .content()
                .doOnNext(full::append)
                .doOnComplete(() -> {
                    observation.highCardinalityKeyValue("completion.length",
                            String.valueOf(full.length()));
                    conversationRepository.save(
                            new Conversation(message, full.toString(), Instant.now()));
                })
                .doOnError(observation::error)
                .doFinally(signalType -> observation.stop());
    }

    /** 建立統一規格的業務 span（chat 與 streamChat 共用，避免標籤重複定義）。 */
    private Observation newChatObservation(String message) {
        return Observation.createNotStarted("chat.service", observationRegistry)
                .contextualName("chat-service")
                .lowCardinalityKeyValue("gen_ai.operation.name", "chat")
                .lowCardinalityKeyValue("gen_ai.system", "openai")
                .lowCardinalityKeyValue("gen_ai.request.model", model)
                .highCardinalityKeyValue("prompt.length", String.valueOf(message.length()));
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
