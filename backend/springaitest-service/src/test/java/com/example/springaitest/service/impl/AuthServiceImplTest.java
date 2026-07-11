package com.example.springaitest.service.impl;

import com.example.springaitest.domain.entity.AppUser;
import com.example.springaitest.domain.entity.Tenant;
import com.example.springaitest.domain.repository.AppUserRepository;
import com.example.springaitest.domain.repository.TenantRepository;
import com.example.springaitest.service.dto.AuthResult;
import com.example.springaitest.service.dto.LoginRequest;
import com.example.springaitest.service.dto.RegisterRequest;
import com.example.springaitest.service.exception.InvalidCredentialsException;
import com.example.springaitest.service.exception.InvalidInviteCodeException;
import com.example.springaitest.service.exception.TenantNotFoundException;
import com.example.springaitest.service.exception.UsernameTakenException;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.ArgumentCaptor;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;
import org.springframework.security.crypto.password.PasswordEncoder;

import java.time.Instant;
import java.util.Optional;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

/**
 * 業務層單元測試：以 Mockito 模擬 repository / 密碼編碼器，
 * 驗證 AuthServiceImpl 的註冊 / 登入邏輯與例外轉換。
 */
@ExtendWith(MockitoExtension.class)
class AuthServiceImplTest {

    @Mock
    private AppUserRepository appUserRepository;

    @Mock
    private TenantRepository tenantRepository;

    @Mock
    private PasswordEncoder passwordEncoder;

    private AuthServiceImpl authService;

    private final Tenant tenant = new Tenant("demo-a", "示範租戶 A", "demo-a-invite", Instant.parse("2026-01-01T00:00:00Z"));

    @Test
    void register_shouldThrowTenantNotFoundWhenTenantMissing() {
        authService = new AuthServiceImpl(appUserRepository, tenantRepository, passwordEncoder);
        when(tenantRepository.findByCode("no-such-tenant")).thenReturn(Optional.empty());

        RegisterRequest request = new RegisterRequest("user-a", "password123", "no-such-tenant", "some-invite");

        assertThatThrownBy(() -> authService.register(request))
                .isInstanceOf(TenantNotFoundException.class)
                .hasMessageContaining("no-such-tenant");

        verify(appUserRepository, never()).save(any());
    }

    @Test
    void register_shouldThrowInvalidInviteCodeWhenInviteCodeWrong() {
        authService = new AuthServiceImpl(appUserRepository, tenantRepository, passwordEncoder);
        when(tenantRepository.findByCode("demo-a")).thenReturn(Optional.of(tenant));

        RegisterRequest request = new RegisterRequest("user-a", "password123", "demo-a", "wrong-invite");

        assertThatThrownBy(() -> authService.register(request))
                .isInstanceOf(InvalidInviteCodeException.class)
                .hasMessageContaining("邀請碼無效");

        verify(appUserRepository, never()).save(any());
    }

    @Test
    void register_shouldThrowUsernameTakenWhenUsernameExists() {
        authService = new AuthServiceImpl(appUserRepository, tenantRepository, passwordEncoder);
        when(tenantRepository.findByCode("demo-a")).thenReturn(Optional.of(tenant));
        when(appUserRepository.existsByUsername("user-a")).thenReturn(true);

        RegisterRequest request = new RegisterRequest("user-a", "password123", "demo-a", "demo-a-invite");

        assertThatThrownBy(() -> authService.register(request))
                .isInstanceOf(UsernameTakenException.class)
                .hasMessageContaining("user-a");

        verify(appUserRepository, never()).save(any());
    }

    @Test
    void register_shouldSaveEncodedPasswordAndReturnUserRole() {
        authService = new AuthServiceImpl(appUserRepository, tenantRepository, passwordEncoder);
        when(tenantRepository.findByCode("demo-a")).thenReturn(Optional.of(tenant));
        when(appUserRepository.existsByUsername("user-a")).thenReturn(false);
        when(passwordEncoder.encode("password123")).thenReturn("hashed-password");

        RegisterRequest request = new RegisterRequest("user-a", "password123", "demo-a", "demo-a-invite");

        AuthResult result = authService.register(request);

        assertThat(result.username()).isEqualTo("user-a");
        assertThat(result.role()).isEqualTo("USER");
        assertThat(result.tenantCode()).isEqualTo("demo-a");

        ArgumentCaptor<AppUser> captor = ArgumentCaptor.forClass(AppUser.class);
        verify(appUserRepository).save(captor.capture());
        assertThat(captor.getValue().getPasswordHash()).isEqualTo("hashed-password");
        assertThat(captor.getValue().getRole()).isEqualTo("USER");
        assertThat(captor.getValue().getTenant()).isEqualTo(tenant);
    }

    @Test
    void login_shouldThrowInvalidCredentialsWhenUserNotFound() {
        authService = new AuthServiceImpl(appUserRepository, tenantRepository, passwordEncoder);
        when(appUserRepository.findByUsername("no-such-user")).thenReturn(Optional.empty());

        LoginRequest request = new LoginRequest("no-such-user", "password123");

        assertThatThrownBy(() -> authService.login(request))
                .isInstanceOf(InvalidCredentialsException.class)
                .hasMessageContaining("帳號或密碼錯誤");
    }

    @Test
    void login_shouldThrowInvalidCredentialsWhenPasswordWrong() {
        authService = new AuthServiceImpl(appUserRepository, tenantRepository, passwordEncoder);
        AppUser user = new AppUser("user-a", "hashed-password", "USER", tenant, Instant.now());
        when(appUserRepository.findByUsername("user-a")).thenReturn(Optional.of(user));
        when(passwordEncoder.matches(eq("wrong-password"), eq("hashed-password"))).thenReturn(false);

        LoginRequest request = new LoginRequest("user-a", "wrong-password");

        assertThatThrownBy(() -> authService.login(request))
                .isInstanceOf(InvalidCredentialsException.class)
                .hasMessageContaining("帳號或密碼錯誤");
    }

    @Test
    void login_shouldReturnAuthResultWhenCredentialsMatch() {
        authService = new AuthServiceImpl(appUserRepository, tenantRepository, passwordEncoder);
        AppUser user = new AppUser("admin-a", "hashed-password", "ADMIN", tenant, Instant.now());
        when(appUserRepository.findByUsername("admin-a")).thenReturn(Optional.of(user));
        when(passwordEncoder.matches(eq("password123"), eq("hashed-password"))).thenReturn(true);

        LoginRequest request = new LoginRequest("admin-a", "password123");

        AuthResult result = authService.login(request);

        assertThat(result).isEqualTo(new AuthResult("admin-a", "ADMIN", "demo-a"));
    }
}
