package com.example.springaitest.service.impl;

import com.example.springaitest.service.WorkflowService;
import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.dto.WorkflowInfo;
import com.example.springaitest.service.dto.WorkflowInvokeResponse;
import com.example.springaitest.service.exception.WorkflowBadInputException;
import com.example.springaitest.service.exception.WorkflowForbiddenException;
import com.example.springaitest.service.exception.WorkflowInvocationException;
import com.example.springaitest.service.exception.WorkflowNotFoundException;
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
 * 業務層實作：透過 RestClient 以 HTTP 觸發外部的 Python（LangGraph）工作流服務，
 * 並同步取得執行結果。
 *
 * 此層只負責協調 HTTP 呼叫與例外轉換，不直接接觸 Controller（交給展示層處理）。
 */
@Service
public class WorkflowServiceImpl implements WorkflowService {

    private final RestClient restClient;
    private final String internalToken;

    @Autowired    // 有兩個建構子時，需明確指定 Spring 要用哪一個注入
    public WorkflowServiceImpl(RestClient.Builder builder,
                               @Value("${workflow.base-url:http://localhost:8000}") String baseUrl,
                               @Value("${workflow.internal-token:internal-dev-token}") String internalToken) {
        this(builder
                .requestFactory(createRequestFactory())
                .baseUrl(baseUrl)
                .build(),
                internalToken);
    }

    /** 測試用：直接注入已組好的 RestClient（例如綁定 MockRestServiceServer 的實例）。 */
    WorkflowServiceImpl(RestClient restClient, String internalToken) {
        this.restClient = restClient;
        this.internalToken = internalToken;
    }

    /**
     * 組出帶逾時設定的請求工廠：
     * 鎖定 HTTP/1.1：JDK HttpClient 預設 HTTP/2，對純 http 目標會先送 h2c 升級請求
     * （Upgrade: h2c + chunked body），uvicorn 會忽略升級但丟失請求 body，導致 422。
     * connectTimeout / readTimeout：避免下游工作流服務卡死（無回應）時，
     * 呼叫端的 servlet 執行緒被無限期佔用而耗盡執行緒池。
     * readTimeout 150 秒 = 下游工作流預設逾時 120 秒 + 餘裕。
     */
    private static JdkClientHttpRequestFactory createRequestFactory() {
        JdkClientHttpRequestFactory requestFactory = new JdkClientHttpRequestFactory(
                HttpClient.newBuilder()
                        .version(HttpClient.Version.HTTP_1_1)
                        .connectTimeout(Duration.ofSeconds(5))
                        .build());
        requestFactory.setReadTimeout(Duration.ofSeconds(150));
        return requestFactory;
    }

    @Override
    public WorkflowInvokeResponse invoke(String name, Map<String, Object> input, UserContext ctx) {
        try {
            return restClient.post()
                    .uri("/workflows/{name}/invoke", name)
                    .headers(headers -> applyHeaders(headers, ctx))
                    .contentType(MediaType.APPLICATION_JSON)
                    .body(Map.of("input", input))
                    .retrieve()
                    .body(WorkflowInvokeResponse.class);
        } catch (HttpClientErrorException.NotFound e) {
            // 下游明確回 404：代表工作流名稱不存在，轉成語意明確的例外。
            throw new WorkflowNotFoundException("找不到工作流：" + name);
        } catch (HttpClientErrorException.Forbidden e) {
            // 下游明確回 403：代表呼叫者角色權限不足。
            throw new WorkflowForbiddenException("權限不足，無法執行工作流：" + name);
        } catch (HttpClientErrorException.UnprocessableEntity e) {
            // 下游明確回 422：代表 input 不符合該工作流的 schema。
            throw new WorkflowBadInputException("工作流輸入不符合規範：" + e.getResponseBodyAsString());
        } catch (RestClientException e) {
            // 其餘情況（連線失敗、下游 500 執行失敗…）一律視為呼叫失敗。
            throw new WorkflowInvocationException("工作流服務呼叫失敗：" + e.getMessage(), e);
        }
    }

    @Override
    public List<WorkflowInfo> list(UserContext ctx) {
        try {
            return restClient.get()
                    .uri("/workflows")
                    .headers(headers -> applyHeaders(headers, ctx))
                    .retrieve()
                    .body(new ParameterizedTypeReference<List<WorkflowInfo>>() {
                    });
        } catch (RestClientException e) {
            throw new WorkflowInvocationException("工作流服務呼叫失敗：" + e.getMessage(), e);
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
