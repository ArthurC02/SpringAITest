package com.example.springaitest.domain.repository;

import com.example.springaitest.domain.entity.AppUser;
import com.example.springaitest.domain.entity.Tenant;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.orm.jpa.DataJpaTest;

import java.time.Instant;
import java.util.Optional;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * 資料層單元測試：用 @DataJpaTest 載入精簡的 JPA 切片（內含 H2），
 * 驗證依使用者名稱查找 / 判斷是否存在的行為。
 */
@DataJpaTest
class AppUserRepositoryTest {

    @Autowired
    private AppUserRepository appUserRepository;

    @Autowired
    private TenantRepository tenantRepository;

    @Test
    void findByUsername_shouldReturnUserWhenExists() {
        Tenant tenant = tenantRepository.save(new Tenant("demo-a", "示範租戶 A", "demo-a-invite", Instant.parse("2026-01-01T00:00:00Z")));
        appUserRepository.save(new AppUser("user-a", "hashed", "USER", tenant, Instant.parse("2026-01-01T00:00:00Z")));

        Optional<AppUser> result = appUserRepository.findByUsername("user-a");

        assertThat(result).isPresent();
        assertThat(result.get().getRole()).isEqualTo("USER");
        assertThat(result.get().getTenant().getCode()).isEqualTo("demo-a");
    }

    @Test
    void existsByUsername_shouldReflectPresence() {
        Tenant tenant = tenantRepository.save(new Tenant("demo-b", "示範租戶 B", "demo-b-invite", Instant.parse("2026-01-01T00:00:00Z")));
        appUserRepository.save(new AppUser("user-b", "hashed", "USER", tenant, Instant.parse("2026-01-01T00:00:00Z")));

        assertThat(appUserRepository.existsByUsername("user-b")).isTrue();
        assertThat(appUserRepository.existsByUsername("no-such-user")).isFalse();
    }
}
