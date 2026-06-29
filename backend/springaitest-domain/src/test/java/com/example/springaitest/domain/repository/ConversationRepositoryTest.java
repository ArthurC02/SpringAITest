package com.example.springaitest.domain.repository;

import com.example.springaitest.domain.entity.Conversation;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.orm.jpa.DataJpaTest;

import java.time.Instant;
import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * 資料層單元測試：用 @DataJpaTest 載入精簡的 JPA 切片（內含 H2），
 * 驗證自訂查詢方法的排序行為。
 */
@DataJpaTest
class ConversationRepositoryTest {

    @Autowired
    private ConversationRepository conversationRepository;

    @Test
    void findAllByOrderByCreatedAtDesc_shouldReturnNewestFirst() {
        Instant now = Instant.parse("2026-01-01T00:00:00Z");
        conversationRepository.save(new Conversation("舊", "舊回覆", now));
        conversationRepository.save(new Conversation("新", "新回覆", now.plusSeconds(60)));

        List<Conversation> result = conversationRepository.findAllByOrderByCreatedAtDesc();

        assertThat(result).hasSize(2);
        assertThat(result).extracting(Conversation::getPrompt)
                .containsExactly("新", "舊");
    }
}
