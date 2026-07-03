package com.example.springaitest.service.impl;

import com.example.springaitest.domain.entity.Conversation;
import com.example.springaitest.domain.repository.ConversationRepository;
import com.example.springaitest.service.ChatService;
import com.example.springaitest.service.dto.ChatResponse;
import io.micrometer.observation.Observation;
import io.micrometer.observation.ObservationRegistry;
import org.springframework.ai.chat.client.ChatClient;
import org.springframework.ai.chat.client.advisor.MessageChatMemoryAdvisor;
import org.springframework.ai.chat.memory.ChatMemory;
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
    private final Mem0Client mem0;
    private final String model;

    public ChatServiceImpl(ChatClient.Builder chatClientBuilder,
                           ConversationRepository conversationRepository,
                           ObservationRegistry observationRegistry,
                           Mem0Client mem0,
                           ChatMemory chatMemory,
                           @Value("${spring.ai.openai.chat.options.model:unknown}") String model) {
        // 短期記憶：掛上 MessageChatMemoryAdvisor（Spring AI 內建，ChatMemory bean 由 auto-config 提供）。
        // 它會在呼叫前把同一 conversationId 的近期對話補進 prompt、呼叫後自動存回，達成「對話內多輪脈絡」。
        // 與 mem0（跨 session 長期事實）互補：mem0 走 system prompt，這裡走對話訊息本身。
        this.chatClient = chatClientBuilder
                .defaultAdvisors(MessageChatMemoryAdvisor.builder(chatMemory).build())
                .build();
        this.conversationRepository = conversationRepository;
        this.observationRegistry = observationRegistry;
        this.mem0 = mem0;
        this.model = model;
    }

    @Override
    @Transactional
    public ChatResponse chat(String message, String userId, String conversationId) {
        String uid = normalizeUser(userId);
        String cid = normalizeConversation(conversationId, uid);
        // 自訂業務 span：包住「呼叫 LLM + 存檔」。span 開始後仍可補標籤，
        // 所以回覆字數在取得回覆後才加上。observe(...) 期間，Spring AI 的
        // ChatClient span 會成為這個 span 的子節點，於 Langfuse 形成巢狀結構。
        Observation observation = newChatObservation(message);

        return observation.observe(() -> {
            String reply = promptWithMemory(uid, cid, message)
                    .call()
                    .content();

            observation.highCardinalityKeyValue("completion.length",
                    String.valueOf(reply == null ? 0 : reply.length()));

            Conversation saved = conversationRepository.save(
                    new Conversation(message, reply, Instant.now()));

            // 回覆完成後把這一輪對話交給 mem0（失敗只記 log，不影響本次結果）。
            mem0.remember(uid, message, reply);

            return toResponse(saved);
        });
    }

    @Override
    public Flux<String> streamChat(String message, String userId, String conversationId) {
        String uid = normalizeUser(userId);
        String cid = normalizeConversation(conversationId, uid);
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
        return promptWithMemory(uid, cid, message)
                .stream()
                .content()
                .doOnNext(full::append)
                .doOnComplete(() -> {
                    observation.highCardinalityKeyValue("completion.length",
                            String.valueOf(full.length()));
                    conversationRepository.save(
                            new Conversation(message, full.toString(), Instant.now()));
                    // 串流結束（最後一個 token 早已送達前端）後才存記憶，
                    // 因此這裡同步呼叫 mem0 不影響使用者感受到的串流延遲。
                    // ponytail: 若 doOnComplete 的收尾延遲要更低，把 remember 丟到獨立 executor。
                    mem0.remember(uid, message, full.toString());
                })
                .doOnError(observation::error)
                .doFinally(signalType -> observation.stop());
    }

    /** 空的 userId 一律歸到 default 使用者，避免記憶查詢/寫入缺識別而報錯。 */
    private String normalizeUser(String userId) {
        return (userId == null || userId.isBlank()) ? "default" : userId;
    }

    /** 空的 conversationId 退回以 userId 當短期記憶分群鍵（至少做到「同一使用者一條對話串」）。 */
    private String normalizeConversation(String conversationId, String fallbackUserId) {
        return (conversationId == null || conversationId.isBlank()) ? fallbackUserId : conversationId;
    }

    /**
     * 組出帶「長期 + 短期記憶」的 prompt spec（chat 與 streamChat 共用）。
     * - 短期：設定 conversationId，讓 MessageChatMemoryAdvisor 補上同對話的近期訊息。
     * - 長期：向 mem0 取回相關事實；有才加一段 system 前言，沒有就維持原樣，
     *   因此 mem0 未啟用或查無記憶時，行為與整合前完全相同。
     */
    private ChatClient.ChatClientRequestSpec promptWithMemory(String userId, String conversationId, String message) {
        ChatClient.ChatClientRequestSpec spec = chatClient.prompt()
                .advisors(a -> a.param(ChatMemory.CONVERSATION_ID, conversationId));
        String memories = mem0.recall(userId, message);
        if (memories != null && !memories.isBlank()) {
            spec = spec.system("以下是你先前記住、關於這位使用者的長期記憶，"
                    + "回答時可參考（與當前問題無關者請忽略）：\n" + memories);
        }
        return spec.user(message);
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
