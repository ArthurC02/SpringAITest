package com.example.springaitest.service.impl;

import com.example.springaitest.service.DocumentService;
import com.example.springaitest.service.dto.DocumentCreateRequest;
import com.example.springaitest.service.dto.DocumentCreated;
import com.example.springaitest.service.dto.DocumentInfo;
import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.exception.DocumentNotFoundException;
import com.example.springaitest.service.exception.WorkflowInvocationException;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.core.ParameterizedTypeReference;
import org.springframework.http.HttpHeaders;
import org.springframework.http.MediaType;
import org.springframework.http.client.JdkClientHttpRequestFactory;
import org.springframework.stereotype.Service;
import org.springframework.web.client.HttpClientErrorException;
import org.springframework.web.client.RestClient;
import org.springframework.web.client.RestClientException;

import java.net.http.HttpClient;
import java.time.Duration;
import java.util.List;
import java.util.Map;

/**
 * 業務層實作：透過 RestClient 以 HTTP 代理至外部的 Python 文件（RAG）服務，
 * 並將呼叫者的租戶 / 身分資訊轉為下游要求的 header。
 *
 * 此層只負責協調 HTTP 呼叫與例外轉換，不直接接觸 Controller（交給展示層處理）。
 */
@Service
public class DocumentServiceImpl implements DocumentService {

    private final RestClient restClient;
    private final String internalToken;

    @Autowired    // 有兩個建構子時，需明確指定 Spring 要用哪一個注入
    public DocumentServiceImpl(RestClient.Builder builder,
                                @Value("${workflow.base-url:http://localhost:8000}") String baseUrl,
                                @Value("${workflow.internal-token:internal-dev-token}") String internalToken) {
        this(builder
                .requestFactory(createRequestFactory())
                .baseUrl(baseUrl)
                .build(),
                internalToken);
    }

    /** 測試用：直接注入已組好的 RestClient（例如綁定 MockRestServiceServer 的實例）。 */
    DocumentServiceImpl(RestClient restClient, String internalToken) {
        this.restClient = restClient;
        this.internalToken = internalToken;
    }

    /**
     * 組出帶逾時設定的請求工廠：
     * 鎖定 HTTP/1.1：JDK HttpClient 預設 HTTP/2，對純 http 目標會先送 h2c 升級請求
     * （Upgrade: h2c + chunked body），uvicorn 會忽略升級但丟失請求 body，導致 422。
     * connectTimeout / readTimeout：避免下游文件服務卡死（無回應）時，
     * 呼叫端的 servlet 執行緒被無限期佔用而耗盡執行緒池。
     * readTimeout 90 秒 = 下游文件端點逾時 60 秒 + 餘裕。
     */
    private static JdkClientHttpRequestFactory createRequestFactory() {
        JdkClientHttpRequestFactory requestFactory = new JdkClientHttpRequestFactory(
                HttpClient.newBuilder()
                        .version(HttpClient.Version.HTTP_1_1)
                        .connectTimeout(Duration.ofSeconds(5))
                        .build());
        requestFactory.setReadTimeout(Duration.ofSeconds(90));
        return requestFactory;
    }

    @Override
    public DocumentCreated create(DocumentCreateRequest request, UserContext ctx) {
        try {
            return restClient.post()
                    .uri("/documents")
                    .headers(headers -> applyHeaders(headers, ctx))
                    .contentType(MediaType.APPLICATION_JSON)
                    .body(Map.of("title", request.title(), "text", request.text()))
                    .retrieve()
                    .body(DocumentCreated.class);
        } catch (RestClientException e) {
            throw new WorkflowInvocationException("文件服務呼叫失敗：" + e.getMessage(), e);
        }
    }

    @Override
    public List<DocumentInfo> list(UserContext ctx) {
        try {
            return restClient.get()
                    .uri("/documents")
                    .headers(headers -> applyHeaders(headers, ctx))
                    .retrieve()
                    .body(new ParameterizedTypeReference<List<DocumentInfo>>() {
                    });
        } catch (RestClientException e) {
            throw new WorkflowInvocationException("文件服務呼叫失敗：" + e.getMessage(), e);
        }
    }

    @Override
    public void delete(String id, UserContext ctx) {
        try {
            restClient.delete()
                    .uri("/documents/{id}", id)
                    .headers(headers -> applyHeaders(headers, ctx))
                    .retrieve()
                    .toBodilessEntity();
        } catch (HttpClientErrorException.NotFound e) {
            // 下游明確回 404：代表文件不存在，轉成語意明確的例外。
            throw new DocumentNotFoundException("找不到文件：" + id);
        } catch (RestClientException e) {
            throw new WorkflowInvocationException("文件服務呼叫失敗：" + e.getMessage(), e);
        }
    }

    /** 將呼叫者的租戶 / 身分資訊，連同內部服務權杖，一併帶到下游的請求 header。 */
    private void applyHeaders(HttpHeaders headers, UserContext ctx) {
        headers.set("X-Internal-Token", internalToken);
        headers.set("X-Tenant-Id", ctx.tenantCode());
        headers.set("X-User-Id", ctx.userId());
        headers.set("X-User-Role", ctx.role());
    }
}
