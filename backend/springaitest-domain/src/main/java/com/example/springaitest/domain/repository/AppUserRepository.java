package com.example.springaitest.domain.repository;

import com.example.springaitest.domain.entity.AppUser;
import org.springframework.data.jpa.repository.JpaRepository;
import org.springframework.stereotype.Repository;

import java.util.Optional;

/**
 * 資料層：負責 AppUser 實體的持久化存取。
 */
@Repository
public interface AppUserRepository extends JpaRepository<AppUser, Long> {

    /** 依使用者名稱查找使用者。 */
    Optional<AppUser> findByUsername(String username);

    /** 判斷使用者名稱是否已被使用。 */
    boolean existsByUsername(String username);
}
