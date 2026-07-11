package com.example.springaitest.security;

import io.jsonwebtoken.Claims;
import io.jsonwebtoken.Jwts;
import io.jsonwebtoken.security.Keys;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;

import javax.crypto.SecretKey;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.Date;

/**
 * 簽發與解析 JWT。演算法固定為 HS256，密鑰由設定檔提供（至少需 32 bytes）。
 * 無效或過期的 token，{@link #parseClaims(String)} 會直接擲出 jjwt 的例外，
 * 由呼叫端（{@link JwtAuthFilter}）決定如何處理，此類別本身不吞例外。
 */
@Service
public class JwtService {

    private final SecretKey key;
    private final long expirationHours;

    public JwtService(@Value("${jwt.secret:dev-jwt-secret-change-me-0123456789abcdef}") String secret,
                       @Value("${jwt.expiration-hours:24}") long expirationHours) {
        this.key = Keys.hmacShaKeyFor(secret.getBytes(StandardCharsets.UTF_8));
        this.expirationHours = expirationHours;
    }

    /** 簽發一枚含使用者名稱、角色、租戶代碼的 JWT。 */
    public String issue(String username, String role, String tenantCode) {
        Instant now = Instant.now();
        return Jwts.builder()
                .subject(username)
                .claim("role", role)
                .claim("tenantCode", tenantCode)
                .issuedAt(Date.from(now))
                .expiration(Date.from(now.plus(expirationHours, ChronoUnit.HOURS)))
                .signWith(key)
                .compact();
    }

    /**
     * 驗證並解析 token，回傳其中的 claims。
     *
     * @throws io.jsonwebtoken.JwtException token 無效（簽章不符、格式錯誤等）或已過期
     */
    public Claims parseClaims(String token) {
        return Jwts.parser()
                .verifyWith(key)
                .build()
                .parseSignedClaims(token)
                .getPayload();
    }
}
