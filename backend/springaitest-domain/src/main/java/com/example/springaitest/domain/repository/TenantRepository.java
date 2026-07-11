package com.example.springaitest.domain.repository;

import com.example.springaitest.domain.entity.Tenant;
import org.springframework.data.jpa.repository.JpaRepository;
import org.springframework.stereotype.Repository;

import java.util.Optional;

/**
 * 資料層：負責 Tenant 實體的持久化存取。
 */
@Repository
public interface TenantRepository extends JpaRepository<Tenant, Long> {

    /** 依租戶代碼查找租戶。 */
    Optional<Tenant> findByCode(String code);
}
