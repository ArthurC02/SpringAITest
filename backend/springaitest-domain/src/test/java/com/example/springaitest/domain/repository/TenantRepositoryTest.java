package com.example.springaitest.domain.repository;

import com.example.springaitest.domain.entity.Tenant;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.orm.jpa.DataJpaTest;

import java.time.Instant;
import java.util.Optional;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * 資料層單元測試：用 @DataJpaTest 載入精簡的 JPA 切片（內含 H2），
 * 驗證依代碼查找租戶的行為。
 */
@DataJpaTest
class TenantRepositoryTest {

    @Autowired
    private TenantRepository tenantRepository;

    @Test
    void findByCode_shouldReturnTenantWhenExists() {
        tenantRepository.save(new Tenant("demo-a", "示範租戶 A", "demo-a-invite", Instant.parse("2026-01-01T00:00:00Z")));

        Optional<Tenant> result = tenantRepository.findByCode("demo-a");

        assertThat(result).isPresent();
        assertThat(result.get().getName()).isEqualTo("示範租戶 A");
    }

    @Test
    void findByCode_shouldReturnEmptyWhenNotExists() {
        Optional<Tenant> result = tenantRepository.findByCode("no-such-tenant");

        assertThat(result).isEmpty();
    }
}
