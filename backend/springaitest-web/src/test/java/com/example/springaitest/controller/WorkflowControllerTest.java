package com.example.springaitest.controller;

import com.example.springaitest.security.AuthenticatedUser;
import com.example.springaitest.security.JwtService;
import com.example.springaitest.service.WorkflowService;
import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.dto.WorkflowInfo;
import com.example.springaitest.service.dto.WorkflowInvokeResponse;
import com.example.springaitest.service.exception.WorkflowNotFoundException;
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
import java.util.Map;

import static org.mockito.ArgumentMatchers.anyMap;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

/**
 * 展示層單元測試：只載入 Web 切片並 mock 掉 Service，
 * 驗證路由、請求驗證與回應序列化。
 * {@code addFilters = false}：@WebMvcTest 不會載入自訂的 SecurityConfig，只會套用 Spring Boot
 * 對 Security 的預設自動配置（逐一要求認證），關閉 filter 鏈以避免這層預設行為干擾。
 * 由於 filter 鏈已關閉，{@code SecurityMockMvcRequestPostProcessors.authentication(...)}
 * （透過 SecurityContextRepository 存回、需要 filter 重新載入）不會生效，
 * 因此改為在測試前後直接操作 {@link SecurityContextHolder}——
 * {@code @AuthenticationPrincipal} 的參數解析器本就是直接讀取它，兩者正好對得上。
 */
@WebMvcTest(WorkflowController.class)
@AutoConfigureMockMvc(addFilters = false)
class WorkflowControllerTest {

    private static final AuthenticatedUser USER_A = new AuthenticatedUser("user-a", "USER", "demo-a");

    @Autowired
    private MockMvc mockMvc;

    @MockitoBean
    private WorkflowService workflowService;

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
    void list_shouldReturnWorkflows() throws Exception {
        when(workflowService.list(eq(new UserContext("user-a", "demo-a", "USER"))))
                .thenReturn(List.of(new WorkflowInfo("summarize", "摘要工作流", "USER")));

        mockMvc.perform(get("/api/workflows"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$[0].name").value("summarize"))
                .andExpect(jsonPath("$[0].description").value("摘要工作流"));
    }

    @Test
    void invoke_shouldReturnWorkflowOutput() throws Exception {
        when(workflowService.invoke(eq("summarize"), anyMap(), eq(new UserContext("user-a", "demo-a", "USER"))))
                .thenReturn(new WorkflowInvokeResponse("summarize", Map.of("summary", "重點摘要")));

        mockMvc.perform(post("/api/workflows/summarize")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"input\":{\"text\":\"一段長文\"}}"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.workflow").value("summarize"))
                .andExpect(jsonPath("$.output.summary").value("重點摘要"));
    }

    @Test
    void invoke_shouldReturn404WhenWorkflowNotFound() throws Exception {
        when(workflowService.invoke(eq("nope"), anyMap(), eq(new UserContext("user-a", "demo-a", "USER"))))
                .thenThrow(new WorkflowNotFoundException("找不到工作流：nope"));

        mockMvc.perform(post("/api/workflows/nope")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"input\":{}}"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.status").value(404));
    }

    @Test
    void invoke_shouldReturn400WhenInputMissing() throws Exception {
        mockMvc.perform(post("/api/workflows/summarize")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{}"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.status").value(400))
                .andExpect(jsonPath("$.fieldErrors.input").exists());
    }
}
