package com.example.springaitest.domain.repository;

import com.example.springaitest.domain.entity.Conversation;
import org.springframework.data.jpa.repository.JpaRepository;
import org.springframework.stereotype.Repository;

import java.util.List;

/**
 * 資料層：負責 Conversation 實體的持久化存取。
 * 繼承 JpaRepository 後即自動具備 CRUD 能力，無需手寫實作。
 */
@Repository
public interface ConversationRepository extends JpaRepository<Conversation, Long> {

    /**
     * 依建立時間由新到舊取出所有對話紀錄。
     * Spring Data 會依方法名稱自動產生查詢。
     */
    List<Conversation> findAllByOrderByCreatedAtDesc();
}
