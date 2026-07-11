package com.example.springaitest.controller;

import com.example.springaitest.security.JwtService;
import com.example.springaitest.service.AuthService;
import com.example.springaitest.service.dto.AuthResult;
import com.example.springaitest.service.dto.LoginRequest;
import com.example.springaitest.service.dto.RegisterRequest;
import jakarta.validation.Valid;
import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

/**
 * 展示層：認證相關的對外 REST API 進入點（註冊 / 登入）。
 * 只負責接收請求 / 簽發 token / 回傳結果，實際的驗證邏輯一律委派給 Service。
 */
@RestController
@RequestMapping("/api/auth")
public class AuthController {

    private final AuthService authService;
    private final JwtService jwtService;

    public AuthController(AuthService authService, JwtService jwtService) {
        this.authService = authService;
        this.jwtService = jwtService;
    }

    /** 註冊新使用者（固定為 USER 角色），須隸屬於既有租戶。 */
    @PostMapping("/register")
    @ResponseStatus(HttpStatus.CREATED)
    public AuthResult register(@Valid @RequestBody RegisterRequest request) {
        return authService.register(request);
    }

    /** 驗證帳號密碼，成功後簽發 JWT。 */
    @PostMapping("/login")
    public LoginResponse login(@Valid @RequestBody LoginRequest request) {
        AuthResult result = authService.login(request);
        String token = jwtService.issue(result.username(), result.role(), result.tenantCode());
        return new LoginResponse(token, result.username(), result.role(), result.tenantCode());
    }
}
