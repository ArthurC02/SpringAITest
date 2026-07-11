package com.example.springaitest.controller;

import com.example.springaitest.security.AuthenticatedUser;
import com.example.springaitest.security.JwtService;
import com.example.springaitest.service.DocumentService;
import com.example.springaitest.service.dto.DocumentCreateRequest;
import com.example.springaitest.service.dto.DocumentCreated;
import com.example.springaitest.service.dto.DocumentInfo;
import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.exception.DocumentNotFoundException;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
import org.springframework.http.MediaType;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.core.authority.SimpleGrantedAuthority;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.test.context.bean.override.mockito.MockitoBean;
import org.springframework.test.web.servlet.MockMvc;

import java.util.List;

import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.doThrow;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.delete;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

/**
 * 展示層單元測試：只載入 Web 切片並 mock 掉 Service，
 * 驗證路由、請求驗證與回應序列化。
 * {@code addFilters = false} + 直接操作 {@link SecurityContextHolder}：見 {@link WorkflowControllerTest} 說明。
 */
@WebMvcTest(DocumentController.class)
@AutoConfigureMockMvc(addFilters = false)
class DocumentControllerTest {

    private static final AuthenticatedUser USER_A = new AuthenticatedUser("user-a", "USER", "demo-a");
    private static final UserContext CTX_A = new UserContext("user-a", "demo-a", "USER");

    @Autowired
    private MockMvc mockMvc;

    @MockitoBean
    private DocumentService documentService;

    // JwtAuthFilter 是 Filter，@WebMvcTest 的切片會自動載入它，因而需要它依賴的 JwtService 也在容器中。
    @MockitoBean
    private JwtService jwtService;

    @BeforeEach
    void authenticateAsUserA() {
        SecurityContextHolder.getContext().setAuthentication(new UsernamePasswordAuthenticationToken(
                USER_A, null, List.of(new SimpleGrantedAuthority("ROLE_" + USER_A.role()))));
    }

    @AfterEach
    void clearSecurityContext() {
        SecurityContextHolder.clearContext();
    }

    @Test
    void create_shouldReturn201WithCreatedDocument() throws Exception {
        when(documentService.create(eq(new DocumentCreateRequest("標題", "內容")), eq(CTX_A)))
                .thenReturn(new DocumentCreated("doc-1", "標題", 3));

        mockMvc.perform(post("/api/documents")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"title\":\"標題\",\"text\":\"內容\"}"))
                .andExpect(status().isCreated())
                .andExpect(jsonPath("$.id").value("doc-1"))
                // chunkCount 標注了 @JsonProperty("chunk_count")，序列化／反序列化都用該名稱。
                .andExpect(jsonPath("$.chunk_count").value(3));
    }

    @Test
    void create_shouldReturn400WhenTitleMissing() throws Exception {
        mockMvc.perform(post("/api/documents")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"title\":\"\",\"text\":\"內容\"}"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.fieldErrors.title").exists());
    }

    @Test
    void list_shouldReturnDocuments() throws Exception {
        when(documentService.list(eq(CTX_A)))
                .thenReturn(List.of(new DocumentInfo("doc-1", "標題", 3, "2026-01-01T00:00:00Z")));

        mockMvc.perform(get("/api/documents"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$[0].id").value("doc-1"))
                .andExpect(jsonPath("$[0].chunk_count").value(3));
    }

    @Test
    void delete_shouldReturn204() throws Exception {
        mockMvc.perform(delete("/api/documents/doc-1"))
                .andExpect(status().isNoContent());
    }

    @Test
    void delete_shouldReturn404WhenDocumentNotFound() throws Exception {
        doThrow(new DocumentNotFoundException("找不到文件：nope"))
                .when(documentService).delete(eq("nope"), eq(CTX_A));

        mockMvc.perform(delete("/api/documents/nope"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.status").value(404));
    }
}
