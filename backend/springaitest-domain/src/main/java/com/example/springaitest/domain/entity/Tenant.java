package com.example.springaitest.domain.entity;

import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.GeneratedValue;
import jakarta.persistence.GenerationType;
import jakarta.persistence.Id;
import jakarta.persistence.Table;

import java.time.Instant;

/**
 * 資料層實體：租戶（多租戶架構的隔離單位）。
 * 對應 H2 中的 tenant 資料表。
 */
@Entity
@Table(name = "tenant")
public class Tenant {

    @Id
    @GeneratedValue(strategy = GenerationType.IDENTITY)
    private Long id;

    @Column(nullable = false, unique = true)
    private String code;

    @Column(nullable = false)
    private String name;

    @Column(nullable = false)
    private String inviteCode;

    @Column(nullable = false)
    private Instant createdAt;

    protected Tenant() {
        // JPA 需要的無參數建構子
    }

    public Tenant(String code, String name, String inviteCode, Instant createdAt) {
        this.code = code;
        this.name = name;
        this.inviteCode = inviteCode;
        this.createdAt = createdAt;
    }

    public Long getId() {
        return id;
    }

    public String getCode() {
        return code;
    }

    public String getName() {
        return name;
    }

    public String getInviteCode() {
        return inviteCode;
    }

    public Instant getCreatedAt() {
        return createdAt;
    }
}
