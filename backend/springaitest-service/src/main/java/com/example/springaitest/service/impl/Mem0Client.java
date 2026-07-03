package com.example.springaitest.service.impl;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Component;
import org.springframework.web.client.RestClient;

import java.util.List;
import java.util.Map;

/**
 * mem0 REST server（mem0/mem0-api-server）的薄封裝。
 * 聊天「前」用 {@link #recall} 取回相關長期記憶塞進 system prompt，
 * 聊天「後」用 {@link #remember} 把這一輪對話交給 mem0（它自行抽取事實）。
 *
 * 設計原則：記憶是「加值」而非「必要路徑」——mem0 掛掉時所有方法都必須靜默降級
 * （recall 回空字串、remember 無動作），絕不可讓聊天主流程失敗。
 */
@Component
public class Mem0Client {

    private static final Logger log = LoggerFactory.getLogger(Mem0Client.class);

    private final RestClient http;

    public Mem0Client(@Value("${mem0.base-url:http://localhost:8000}") String baseUrl) {
        this.http = RestClient.create(baseUrl);
    }

    /** 取回與本次訊息相關的記憶，串成一段條列文字；查無或失敗都回空字串。 */
    @SuppressWarnings("unchecked")
    public String recall(String userId, String query) {
        try {
            Map<String, Object> body = http.post()
                    .uri("/search")
                    .body(Map.of("query", query, "user_id", userId, "top_k", 5))
                    .retrieve()
                    .body(Map.class);
            List<Map<String, Object>> results = body == null ? null
                    : (List<Map<String, Object>>) body.get("results");
            if (results == null || results.isEmpty()) return "";
            StringBuilder sb = new StringBuilder();
            for (Map<String, Object> r : results) {
                Object memory = r.get("memory");
                if (memory != null) sb.append("- ").append(memory).append('\n');
            }
            return sb.toString();
        } catch (Exception e) {
            log.warn("mem0 recall 失敗，改用空記憶：{}", e.getMessage());
            return "";
        }
    }

    /** 把一輪對話交給 mem0 抽取並保存；失敗僅記 log，不影響主流程。 */
    public void remember(String userId, String userMessage, String aiReply) {
        try {
            http.post()
                    .uri("/memories")
                    .body(Map.of(
                            "user_id", userId,
                            "messages", List.of(
                                    Map.of("role", "user", "content", userMessage),
                                    Map.of("role", "assistant", "content", aiReply))))
                    .retrieve()
                    .toBodilessEntity();
        } catch (Exception e) {
            log.warn("mem0 remember 失敗，跳過本輪記憶：{}", e.getMessage());
        }
    }
}
