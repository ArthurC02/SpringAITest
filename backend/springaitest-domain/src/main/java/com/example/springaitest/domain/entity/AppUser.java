package com.example.springaitest.domain.entity;

import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.FetchType;
import jakarta.persistence.GeneratedValue;
import jakarta.persistence.GenerationType;
import jakarta.persistence.Id;
import jakarta.persistence.JoinColumn;
import jakarta.persistence.ManyToOne;
import jakarta.persistence.Table;

import java.time.Instant;

/**
 * 資料層實體：應用程式使用者，隸屬於單一租戶。
 * 對應 H2 中的 app_user 資料表。
 */
@Entity
@Table(name = "app_user")
public class AppUser {

    @Id
    @GeneratedValue(strategy = GenerationType.IDENTITY)
    private Long id;

    @Column(nullable = false, unique = true)
    private String username;

    @Column(nullable = false)
    private String passwordHash;

    /** 角色（存放 enum 名稱字串，如 "USER"、"ADMIN"），避免額外引入 @Enumerated 的複雜度。 */
    @Column(nullable = false)
    private String role;

    @ManyToOne(optional = false, fetch = FetchType.LAZY)
    @JoinColumn(name = "tenant_id", nullable = false)
    private Tenant tenant;

    @Column(nullable = false)
    private Instant createdAt;

    protected AppUser() {
        // JPA 需要的無參數建構子
    }

    public AppUser(String username, String passwordHash, String role, Tenant tenant, Instant createdAt) {
        this.username = username;
        this.passwordHash = passwordHash;
        this.role = role;
        this.tenant = tenant;
        this.createdAt = createdAt;
    }

    public Long getId() {
        return id;
    }

    public String getUsername() {
        return username;
    }

    public String getPasswordHash() {
        return passwordHash;
    }

    public String getRole() {
        return role;
    }

    public Tenant getTenant() {
        return tenant;
    }

    public Instant getCreatedAt() {
        return createdAt;
    }
}
