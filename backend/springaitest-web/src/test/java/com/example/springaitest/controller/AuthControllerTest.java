package com.example.springaitest.controller;

import com.example.springaitest.security.JwtService;
import com.example.springaitest.service.AuthService;
import com.example.springaitest.service.dto.AuthResult;
import com.example.springaitest.service.dto.LoginRequest;
import com.example.springaitest.service.dto.RegisterRequest;
import com.example.springaitest.service.exception.InvalidCredentialsException;
import com.example.springaitest.service.exception.InvalidInviteCodeException;
import com.example.springaitest.service.exception.TenantNotFoundException;
import com.example.springaitest.service.exception.UsernameTakenException;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
import org.springframework.http.MediaType;
import org.springframework.test.context.bean.override.mockito.MockitoBean;
import org.springframework.test.web.servlet.MockMvc;

import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

/**
 * 展示層單元測試：只載入 Web 切片並 mock 掉 Service，
 * 驗證路由、請求驗證與回應序列化。
 * /api/auth/** 在 SecurityConfig 中維持公開，但 @WebMvcTest 不會載入 SecurityConfig，
 * 因此關閉 filter 鏈以還原「公開路由」的行為（見 {@link WorkflowControllerTest} 說明）。
 */
@WebMvcTest(AuthController.class)
@AutoConfigureMockMvc(addFilters = false)
class AuthControllerTest {

    @Autowired
    private MockMvc mockMvc;

    @MockitoBean
    private AuthService authService;

    @MockitoBean
    private JwtService jwtService;

    @Test
    void register_shouldReturn201WithAuthResult() throws Exception {
        when(authService.register(eq(new RegisterRequest("user-a", "password123", "demo-a", "demo-a-invite"))))
                .thenReturn(new AuthResult("user-a", "USER", "demo-a"));

        mockMvc.perform(post("/api/auth/register")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"password123\",\"tenantCode\":\"demo-a\",\"inviteCode\":\"demo-a-invite\"}"))
                .andExpect(status().isCreated())
                .andExpect(jsonPath("$.username").value("user-a"))
                .andExpect(jsonPath("$.role").value("USER"))
                .andExpect(jsonPath("$.tenantCode").value("demo-a"));
    }

    @Test
    void register_shouldReturn400WhenPasswordTooShort() throws Exception {
        mockMvc.perform(post("/api/auth/register")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"short\",\"tenantCode\":\"demo-a\",\"inviteCode\":\"demo-a-invite\"}"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.fieldErrors.password").exists());
    }

    @Test
    void register_shouldReturn400WhenInviteCodeMissing() throws Exception {
        mockMvc.perform(post("/api/auth/register")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"password123\",\"tenantCode\":\"demo-a\"}"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.fieldErrors.inviteCode").exists());
    }

    @Test
    void register_shouldReturn404WhenTenantNotFound() throws Exception {
        when(authService.register(eq(new RegisterRequest("user-a", "password123", "no-such-tenant", "some-invite"))))
                .thenThrow(new TenantNotFoundException("找不到租戶：no-such-tenant"));

        mockMvc.perform(post("/api/auth/register")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"password123\",\"tenantCode\":\"no-such-tenant\",\"inviteCode\":\"some-invite\"}"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.status").value(404));
    }

    @Test
    void register_shouldReturn403WhenInviteCodeInvalid() throws Exception {
        when(authService.register(eq(new RegisterRequest("user-a", "password123", "demo-a", "wrong-invite"))))
                .thenThrow(new InvalidInviteCodeException("邀請碼無效"));

        mockMvc.perform(post("/api/auth/register")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"password123\",\"tenantCode\":\"demo-a\",\"inviteCode\":\"wrong-invite\"}"))
                .andExpect(status().isForbidden())
                .andExpect(jsonPath("$.status").value(403));
    }

    @Test
    void register_shouldReturn409WhenUsernameTaken() throws Exception {
        when(authService.register(eq(new RegisterRequest("user-a", "password123", "demo-a", "demo-a-invite"))))
                .thenThrow(new UsernameTakenException("使用者名稱已存在：user-a"));

        mockMvc.perform(post("/api/auth/register")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"password123\",\"tenantCode\":\"demo-a\",\"inviteCode\":\"demo-a-invite\"}"))
                .andExpect(status().isConflict())
                .andExpect(jsonPath("$.status").value(409));
    }

    @Test
    void login_shouldReturnTokenAndUserInfo() throws Exception {
        when(authService.login(eq(new LoginRequest("user-a", "password123"))))
                .thenReturn(new AuthResult("user-a", "USER", "demo-a"));
        when(jwtService.issue("user-a", "USER", "demo-a")).thenReturn("stub-jwt-token");

        mockMvc.perform(post("/api/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"password123\"}"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.token").value("stub-jwt-token"))
                .andExpect(jsonPath("$.username").value("user-a"))
                .andExpect(jsonPath("$.role").value("USER"))
                .andExpect(jsonPath("$.tenantCode").value("demo-a"));
    }

    @Test
    void login_shouldReturn401WhenCredentialsInvalid() throws Exception {
        when(authService.login(eq(new LoginRequest("user-a", "wrong-password"))))
                .thenThrow(new InvalidCredentialsException("帳號或密碼錯誤"));

        mockMvc.perform(post("/api/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"user-a\",\"password\":\"wrong-password\"}"))
                .andExpect(status().isUnauthorized())
                .andExpect(jsonPath("$.status").value(401));
    }
}
