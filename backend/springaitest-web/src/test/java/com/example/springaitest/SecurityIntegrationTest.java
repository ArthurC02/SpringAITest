package com.example.springaitest;

import com.example.springaitest.controller.LoginResponse;
import com.example.springaitest.exception.ApiError;
import com.example.springaitest.service.WorkflowService;
import com.example.springaitest.service.dto.AuthResult;
import com.example.springaitest.service.dto.WorkflowInfo;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.web.client.TestRestTemplate;
import org.springframework.http.HttpEntity;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpMethod;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.test.context.bean.override.mockito.MockitoBean;

import java.util.List;
import java.util.Map;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.when;

/**
 * 端對端安全測試：以真實的 HTTP 呼叫（{@link TestRestTemplate}）驗證整條認證鏈路——
 * 未帶 token 一律 401（且形狀仍是既有的 ApiError）、公開路由（/api/chat/**）不受影響、
 * 以及「註冊 → 登入 → 帶 token 呼叫受保護 API」的完整流程。
 * WorkflowService 以 {@link MockitoBean} 取代，避免測試依賴真正的下游工作流服務。
 */
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
class SecurityIntegrationTest {

    @Autowired
    private TestRestTemplate restTemplate;

    @MockitoBean
    private WorkflowService workflowService;

    @Test
    void workflows_shouldReturn401WithApiErrorShapeWhenTokenMissing() {
        ResponseEntity<ApiError> response = restTemplate.getForEntity("/api/workflows", ApiError.class);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(response.getBody()).isNotNull();
        assertThat(response.getBody().status()).isEqualTo(401);
        assertThat(response.getBody().message()).isEqualTo("未認證或憑證無效");
    }

    @Test
    void chatHistory_shouldReturn200WithoutTokenBecausePublic() {
        ResponseEntity<String> response = restTemplate.getForEntity("/api/chat/history", String.class);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.OK);
    }

    @Test
    void registerThenLoginThenCallProtectedApi_shouldSucceedWithBearerToken() {
        Map<String, String> registerBody = Map.of(
                "username", "it-newuser",
                "password", "password123",
                "tenantCode", "demo-a",
                "inviteCode", "demo-a-invite");
        ResponseEntity<AuthResult> registerResponse =
                restTemplate.postForEntity("/api/auth/register", registerBody, AuthResult.class);

        assertThat(registerResponse.getStatusCode()).isEqualTo(HttpStatus.CREATED);
        assertThat(registerResponse.getBody()).isNotNull();
        assertThat(registerResponse.getBody().role()).isEqualTo("USER");

        Map<String, String> loginBody = Map.of(
                "username", "it-newuser",
                "password", "password123");
        ResponseEntity<LoginResponse> loginResponse =
                restTemplate.postForEntity("/api/auth/login", loginBody, LoginResponse.class);

        assertThat(loginResponse.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(loginResponse.getBody()).isNotNull();
        String token = loginResponse.getBody().token();
        assertThat(token).isNotBlank();

        when(workflowService.list(any()))
                .thenReturn(List.of(new WorkflowInfo("summarize", "摘要工作流", "USER")));

        HttpHeaders headers = new HttpHeaders();
        headers.setBearerAuth(token);
        ResponseEntity<WorkflowInfo[]> workflowsResponse = restTemplate.exchange(
                "/api/workflows", HttpMethod.GET, new HttpEntity<>(headers), WorkflowInfo[].class);

        assertThat(workflowsResponse.getStatusCode()).isEqualTo(HttpStatus.OK);
        assertThat(workflowsResponse.getBody()).hasSize(1);
        assertThat(workflowsResponse.getBody()[0].name()).isEqualTo("summarize");
    }

    @Test
    void register_shouldReturn403WhenInviteCodeInvalid() {
        Map<String, String> registerBody = Map.of(
                "username", "it-baduser",
                "password", "password123",
                "tenantCode", "demo-a",
                "inviteCode", "wrong-invite");
        ResponseEntity<ApiError> response =
                restTemplate.postForEntity("/api/auth/register", registerBody, ApiError.class);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.FORBIDDEN);
        assertThat(response.getBody()).isNotNull();
        assertThat(response.getBody().message()).isEqualTo("邀請碼無效");
    }

    @Test
    void login_shouldReturn401WhenPasswordWrong() {
        Map<String, String> loginBody = Map.of(
                "username", "user-a",
                "password", "wrong-password");
        ResponseEntity<ApiError> response =
                restTemplate.postForEntity("/api/auth/login", loginBody, ApiError.class);

        assertThat(response.getStatusCode()).isEqualTo(HttpStatus.UNAUTHORIZED);
        assertThat(response.getBody()).isNotNull();
        assertThat(response.getBody().message()).isEqualTo("帳號或密碼錯誤");
    }
}
