package com.example.springaitest.service.impl;

import com.example.springaitest.service.dto.DocumentCreateRequest;
import com.example.springaitest.service.dto.DocumentCreated;
import com.example.springaitest.service.dto.DocumentInfo;
import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.exception.DocumentNotFoundException;
import com.example.springaitest.service.exception.WorkflowInvocationException;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.http.MediaType;
import org.springframework.test.web.client.MockRestServiceServer;
import org.springframework.web.client.RestClient;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.springframework.http.HttpMethod.DELETE;
import static org.springframework.http.HttpMethod.GET;
import static org.springframework.http.HttpMethod.POST;
import static org.springframework.http.HttpStatus.NOT_FOUND;
import static org.springframework.http.HttpStatus.NO_CONTENT;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.header;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.method;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.requestTo;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withServerError;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withStatus;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withSuccess;

/**
 * 業務層單元測試：以 {@link MockRestServiceServer} 模擬文件服務的 HTTP 回應，
 * 驗證 DocumentServiceImpl 的請求組裝（含身分 header）與例外轉換邏輯。
 */
class DocumentServiceImplTest {

    private static final String INTERNAL_TOKEN = "test-internal-token";
    private static final UserContext CTX = new UserContext("user-a", "demo-a", "USER");

    private MockRestServiceServer server;
    private DocumentServiceImpl documentService;

    @BeforeEach
    void setUp() {
        // 用測試建構子直接注入綁定 mock 的 RestClient：
        // 正式建構子會覆寫 requestFactory（鎖 HTTP/1.1），會蓋掉 bindTo 綁定的 mock factory。
        RestClient.Builder builder = RestClient.builder();
        server = MockRestServiceServer.bindTo(builder).build();
        documentService = new DocumentServiceImpl(
                builder.baseUrl("http://localhost:8000").build(), INTERNAL_TOKEN);
    }

    @Test
    void create_shouldReturnMappedResponseAndSendIdentityHeaders() {
        server.expect(requestTo("http://localhost:8000/documents"))
                .andExpect(method(POST))
                .andExpect(header("X-Internal-Token", INTERNAL_TOKEN))
                .andExpect(header("X-Tenant-Id", "demo-a"))
                .andExpect(header("X-User-Id", "user-a"))
                .andExpect(header("X-User-Role", "USER"))
                .andRespond(withStatus(org.springframework.http.HttpStatus.CREATED)
                        .contentType(MediaType.APPLICATION_JSON)
                        .body("{\"id\":\"doc-1\",\"title\":\"標題\",\"chunk_count\":3}"));

        DocumentCreated result = documentService.create(new DocumentCreateRequest("標題", "內容"), CTX);

        assertThat(result.id()).isEqualTo("doc-1");
        assertThat(result.title()).isEqualTo("標題");
        assertThat(result.chunkCount()).isEqualTo(3);
    }

    @Test
    void list_shouldReturnMappedDocumentInfosAndSendIdentityHeaders() {
        server.expect(requestTo("http://localhost:8000/documents"))
                .andExpect(method(GET))
                .andExpect(header("X-Internal-Token", INTERNAL_TOKEN))
                .andExpect(header("X-Tenant-Id", "demo-a"))
                .andExpect(header("X-User-Id", "user-a"))
                .andExpect(header("X-User-Role", "USER"))
                .andRespond(withSuccess(
                        "[{\"id\":\"doc-1\",\"title\":\"標題\",\"chunk_count\":3,\"created_at\":\"2026-01-01T00:00:00Z\"}]",
                        MediaType.APPLICATION_JSON));

        var result = documentService.list(CTX);

        assertThat(result).containsExactly(
                new DocumentInfo("doc-1", "標題", 3, "2026-01-01T00:00:00Z"));
    }

    @Test
    void delete_shouldSendIdentityHeadersAndCompleteOn204() {
        server.expect(requestTo("http://localhost:8000/documents/doc-1"))
                .andExpect(method(DELETE))
                .andExpect(header("X-Internal-Token", INTERNAL_TOKEN))
                .andExpect(header("X-Tenant-Id", "demo-a"))
                .andExpect(header("X-User-Id", "user-a"))
                .andExpect(header("X-User-Role", "USER"))
                .andRespond(withStatus(NO_CONTENT));

        documentService.delete("doc-1", CTX);

        server.verify();
    }

    @Test
    void delete_shouldThrowDocumentNotFoundWhenDownstream404() {
        server.expect(requestTo("http://localhost:8000/documents/nope"))
                .andExpect(method(DELETE))
                .andRespond(withStatus(NOT_FOUND));

        assertThatThrownBy(() -> documentService.delete("nope", CTX))
                .isInstanceOf(DocumentNotFoundException.class)
                .hasMessageContaining("nope");
    }

    @Test
    void create_shouldThrowWorkflowInvocationExceptionWhenDownstream500() {
        server.expect(requestTo("http://localhost:8000/documents"))
                .andExpect(method(POST))
                .andRespond(withServerError());

        assertThatThrownBy(() -> documentService.create(new DocumentCreateRequest("標題", "內容"), CTX))
                .isInstanceOf(WorkflowInvocationException.class);
    }
}
