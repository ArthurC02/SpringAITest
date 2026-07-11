package com.example.springaitest.service;

import com.example.springaitest.service.dto.AuthResult;
import com.example.springaitest.service.dto.LoginRequest;
import com.example.springaitest.service.dto.RegisterRequest;

/**
 * 業務層介面：定義使用者註冊 / 登入相關的業務行為。
 */
public interface AuthService {

    /**
     * 註冊新使用者（固定為 USER 角色），須隸屬於既有租戶。
     *
     * @param request 註冊請求（username、password、tenantCode）
     * @return 註冊成功後的使用者資訊
     */
    AuthResult register(RegisterRequest request);

    /**
     * 驗證帳號密碼並取得使用者資訊。
     *
     * @param request 登入請求（username、password）
     * @return 登入成功後的使用者資訊
     */
    AuthResult login(LoginRequest request);
}
