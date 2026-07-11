package com.example.springaitest.service.impl;

import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.dto.WorkflowInfo;
import com.example.springaitest.service.dto.WorkflowInvokeResponse;
import com.example.springaitest.service.exception.WorkflowBadInputException;
import com.example.springaitest.service.exception.WorkflowForbiddenException;
import com.example.springaitest.service.exception.WorkflowInvocationException;
import com.example.springaitest.service.exception.WorkflowNotFoundException;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.http.MediaType;
import org.springframework.test.web.client.MockRestServiceServer;
import org.springframework.web.client.RestClient;

import java.util.Map;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.springframework.http.HttpMethod.GET;
import static org.springframework.http.HttpMethod.POST;
import static org.springframework.http.HttpStatus.FORBIDDEN;
import static org.springframework.http.HttpStatus.NOT_FOUND;
import static org.springframework.http.HttpStatus.UNPROCESSABLE_ENTITY;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.header;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.method;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.requestTo;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withServerError;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withStatus;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withSuccess;

/**
 * 業務層單元測試：以 {@link MockRestServiceServer} 模擬工作流服務的 HTTP 回應，
 * 驗證 WorkflowServiceImpl 的請求組裝（含身分 header）與例外轉換邏輯。
 */
class WorkflowServiceImplTest {

    private static final String INTERNAL_TOKEN = "test-internal-token";
    private static final UserContext CTX = new UserContext("user-a", "demo-a", "USER");

    private MockRestServiceServer server;
    private WorkflowServiceImpl workflowService;

    @BeforeEach
    void setUp() {
        // 用測試建構子直接注入綁定 mock 的 RestClient：
        // 正式建構子會覆寫 requestFactory（鎖 HTTP/1.1），會蓋掉 bindTo 綁定的 mock factory。
        RestClient.Builder builder = RestClient.builder();
        server = MockRestServiceServer.bindTo(builder).build();
        workflowService = new WorkflowServiceImpl(
                builder.baseUrl("http://localhost:8000").build(), INTERNAL_TOKEN);
    }

    @Test
    void invoke_shouldReturnMappedResponseAndSendIdentityHeaders() {
        server.expect(requestTo("http://localhost:8000/workflows/summarize/invoke"))
                .andExpect(method(POST))
                .andExpect(header("X-Internal-Token", INTERNAL_TOKEN))
                .andExpect(header("X-Tenant-Id", "demo-a"))
                .andExpect(header("X-User-Id", "user-a"))
                .andExpect(header("X-User-Role", "USER"))
                .andRespond(withSuccess(
                        "{\"workflow\":\"summarize\",\"output\":{\"summary\":\"重點摘要\"}}",
                        MediaType.APPLICATION_JSON));

        WorkflowInvokeResponse result = workflowService.invoke("summarize", Map.of("text", "一段長文"), CTX);

        assertThat(result.workflow()).isEqualTo("summarize");
        assertThat(result.output()).containsEntry("summary", "重點摘要");
    }

    @Test
    void invoke_shouldThrowWorkflowNotFoundWhenDownstream404() {
        server.expect(requestTo("http://localhost:8000/workflows/nope/invoke"))
                .andExpect(method(POST))
                .andRespond(withStatus(NOT_FOUND)
                        .contentType(MediaType.APPLICATION_JSON)
                        .body("{\"error\":\"workflow_not_found\"}"));

        assertThatThrownBy(() -> workflowService.invoke("nope", Map.of(), CTX))
                .isInstanceOf(WorkflowNotFoundException.class)
                .hasMessageContaining("nope");
    }

    @Test
    void invoke_shouldThrowWorkflowForbiddenWhenDownstream403() {
        server.expect(requestTo("http://localhost:8000/workflows/admin-only/invoke"))
                .andExpect(method(POST))
                .andRespond(withStatus(FORBIDDEN)
                        .contentType(MediaType.APPLICATION_JSON)
                        .body("{\"error\":\"forbidden\"}"));

        assertThatThrownBy(() -> workflowService.invoke("admin-only", Map.of(), CTX))
                .isInstanceOf(WorkflowForbiddenException.class)
                .hasMessageContaining("admin-only");
    }

    @Test
    void invoke_shouldThrowWorkflowBadInputWhenDownstream422() {
        server.expect(requestTo("http://localhost:8000/workflows/summarize/invoke"))
                .andExpect(method(POST))
                .andRespond(withStatus(UNPROCESSABLE_ENTITY)
                        .contentType(MediaType.APPLICATION_JSON)
                        .body("{\"detail\":\"text 欄位必填\"}"));

        assertThatThrownBy(() -> workflowService.invoke("summarize", Map.of(), CTX))
                .isInstanceOf(WorkflowBadInputException.class)
                .hasMessageContaining("text 欄位必填");
    }

    @Test
    void invoke_shouldThrowWorkflowInvocationExceptionWhenDownstream500() {
        server.expect(requestTo("http://localhost:8000/workflows/summarize/invoke"))
                .andExpect(method(POST))
                .andRespond(withServerError());

        assertThatThrownBy(() -> workflowService.invoke("summarize", Map.of("text", "一段長文"), CTX))
                .isInstanceOf(WorkflowInvocationException.class);
    }

    @Test
    void list_shouldReturnMappedWorkflowInfosAndSendIdentityHeaders() {
        server.expect(requestTo("http://localhost:8000/workflows"))
                .andExpect(method(GET))
                .andExpect(header("X-Internal-Token", INTERNAL_TOKEN))
                .andExpect(header("X-Tenant-Id", "demo-a"))
                .andExpect(header("X-User-Id", "user-a"))
                .andExpect(header("X-User-Role", "USER"))
                .andRespond(withSuccess(
                        "[{\"name\":\"summarize\",\"description\":\"摘要工作流\",\"required_role\":\"USER\"},"
                                + "{\"name\":\"triage\",\"description\":\"分流工作流\",\"required_role\":\"ADMIN\"}]",
                        MediaType.APPLICATION_JSON));

        var result = workflowService.list(CTX);

        assertThat(result).containsExactly(
                new WorkflowInfo("summarize", "摘要工作流", "USER"),
                new WorkflowInfo("triage", "分流工作流", "ADMIN"));
    }
}
