package com.example.springaitest.config;

import com.example.springaitest.domain.entity.AppUser;
import com.example.springaitest.domain.entity.Tenant;
import com.example.springaitest.domain.repository.AppUserRepository;
import com.example.springaitest.domain.repository.TenantRepository;
import org.springframework.boot.CommandLineRunner;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Component;
import org.springframework.transaction.annotation.Transactional;

import java.time.Instant;

/**
 * 開發用種子資料：啟動時確保示範租戶與示範使用者存在（以 existsByUsername 判斷是否已建立，可重複執行）。
 * 密碼一律為 password123（BCrypt 編碼後存入）。
 */
@Component
public class DataInitializer implements CommandLineRunner {

    private static final String DEFAULT_PASSWORD = "password123";

    private final TenantRepository tenantRepository;
    private final AppUserRepository appUserRepository;
    private final PasswordEncoder passwordEncoder;

    public DataInitializer(TenantRepository tenantRepository,
                            AppUserRepository appUserRepository,
                            PasswordEncoder passwordEncoder) {
        this.tenantRepository = tenantRepository;
        this.appUserRepository = appUserRepository;
        this.passwordEncoder = passwordEncoder;
    }

    @Override
    @Transactional
    public void run(String... args) {
        Tenant tenantA = ensureTenant("demo-a", "示範租戶 A", "demo-a-invite");
        Tenant tenantB = ensureTenant("demo-b", "示範租戶 B", "demo-b-invite");

        ensureUser("admin-a", "ADMIN", tenantA);
        ensureUser("user-a", "USER", tenantA);
        ensureUser("user-b", "USER", tenantB);
    }

    private Tenant ensureTenant(String code, String name, String inviteCode) {
        return tenantRepository.findByCode(code)
                .orElseGet(() -> tenantRepository.save(new Tenant(code, name, inviteCode, Instant.now())));
    }

    private void ensureUser(String username, String role, Tenant tenant) {
        if (!appUserRepository.existsByUsername(username)) {
            appUserRepository.save(new AppUser(
                    username,
                    passwordEncoder.encode(DEFAULT_PASSWORD),
                    role,
                    tenant,
                    Instant.now()));
        }
    }
}
