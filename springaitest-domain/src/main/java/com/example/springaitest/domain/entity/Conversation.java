package com.example.springaitest.domain.entity;

import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.GeneratedValue;
import jakarta.persistence.GenerationType;
import jakarta.persistence.Id;
import jakarta.persistence.Lob;
import jakarta.persistence.Table;

import java.time.Instant;

/**
 * 資料層實體：一筆對話紀錄（使用者輸入 + AI 回覆）。
 * 對應 H2 中的 conversation 資料表。
 */
@Entity
@Table(name = "conversation")
public class Conversation {

    @Id
    @GeneratedValue(strategy = GenerationType.IDENTITY)
    private Long id;

    @Lob
    @Column(nullable = false)
    private String prompt;

    @Lob
    @Column(nullable = false)
    private String reply;

    @Column(nullable = false)
    private Instant createdAt;

    protected Conversation() {
        // JPA 需要的無參數建構子
    }

    public Conversation(String prompt, String reply, Instant createdAt) {
        this.prompt = prompt;
        this.reply = reply;
        this.createdAt = createdAt;
    }

    public Long getId() {
        return id;
    }

    public String getPrompt() {
        return prompt;
    }

    public String getReply() {
        return reply;
    }

    public Instant getCreatedAt() {
        return createdAt;
    }
}
