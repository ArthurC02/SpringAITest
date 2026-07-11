package com.example.springaitest.service.impl;

import com.example.springaitest.domain.entity.AppUser;
import com.example.springaitest.domain.entity.Tenant;
import com.example.springaitest.domain.repository.AppUserRepository;
import com.example.springaitest.domain.repository.TenantRepository;
import com.example.springaitest.service.AuthService;
import com.example.springaitest.service.dto.AuthResult;
import com.example.springaitest.service.dto.LoginRequest;
import com.example.springaitest.service.dto.RegisterRequest;
import com.example.springaitest.service.exception.InvalidCredentialsException;
import com.example.springaitest.service.exception.InvalidInviteCodeException;
import com.example.springaitest.service.exception.TenantNotFoundException;
import com.example.springaitest.service.exception.UsernameTakenException;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.time.Instant;

/**
 * 業務層實作：使用者註冊 / 登入。
 * 密碼一律以 BCrypt 編碼儲存，此層只負責協調 repository 存取與例外轉換。
 */
@Service
public class AuthServiceImpl implements AuthService {

    private static final String DEFAULT_ROLE = "USER";

    private final AppUserRepository appUserRepository;
    private final TenantRepository tenantRepository;
    private final PasswordEncoder passwordEncoder;

    public AuthServiceImpl(AppUserRepository appUserRepository,
                            TenantRepository tenantRepository,
                            PasswordEncoder passwordEncoder) {
        this.appUserRepository = appUserRepository;
        this.tenantRepository = tenantRepository;
        this.passwordEncoder = passwordEncoder;
    }

    @Override
    @Transactional
    public AuthResult register(RegisterRequest request) {
        Tenant tenant = tenantRepository.findByCode(request.tenantCode())
                .orElseThrow(() -> new TenantNotFoundException("找不到租戶：" + request.tenantCode()));

        if (!tenant.getInviteCode().equals(request.inviteCode())) {
            // 邀請碼制：只有持有租戶邀請碼的人才能加入該租戶，避免任何匿名者自助加入任意租戶。
            throw new InvalidInviteCodeException("邀請碼無效");
        }

        if (appUserRepository.existsByUsername(request.username())) {
            throw new UsernameTakenException("使用者名稱已存在：" + request.username());
        }

        AppUser user = new AppUser(
                request.username(),
                passwordEncoder.encode(request.password()),
                DEFAULT_ROLE,
                tenant,
                Instant.now());
        appUserRepository.save(user);

        return new AuthResult(user.getUsername(), user.getRole(), tenant.getCode());
    }

    @Override
    @Transactional(readOnly = true)
    public AuthResult login(LoginRequest request) {
        AppUser user = appUserRepository.findByUsername(request.username())
                .orElseThrow(() -> new InvalidCredentialsException("帳號或密碼錯誤"));

        if (!passwordEncoder.matches(request.password(), user.getPasswordHash())) {
            throw new InvalidCredentialsException("帳號或密碼錯誤");
        }

        return new AuthResult(user.getUsername(), user.getRole(), user.getTenant().getCode());
    }
}
