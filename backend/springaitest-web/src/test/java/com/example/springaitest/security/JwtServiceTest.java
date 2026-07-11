package com.example.springaitest.security;

import io.jsonwebtoken.Claims;
import io.jsonwebtoken.ExpiredJwtException;
import io.jsonwebtoken.JwtException;
import io.jsonwebtoken.security.Keys;
import io.jsonwebtoken.security.SignatureException;
import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.util.Date;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

/**
 * 單元測試：驗證 JwtService 的簽發 / 解析，含過期與竄改的情境。
 */
class JwtServiceTest {

    private static final String SECRET = "dev-jwt-secret-change-me-0123456789abcdef";

    private final JwtService jwtService = new JwtService(SECRET, 24);

    @Test
    void issueAndParse_shouldRoundTripClaims() {
        String token = jwtService.issue("user-a", "USER", "demo-a");

        Claims claims = jwtService.parseClaims(token);

        assertThat(claims.getSubject()).isEqualTo("user-a");
        assertThat(claims.get("role", String.class)).isEqualTo("USER");
        assertThat(claims.get("tenantCode", String.class)).isEqualTo("demo-a");
    }

    @Test
    void parseClaims_shouldThrowWhenTokenExpired() {
        // 直接用同一支密鑰，手刻一枚「已過期」的 token（issuedAt / expiration 都在過去）。
        JwtService shortLivedIssuer = new JwtService(SECRET, 24);
        Instant past = Instant.now().minusSeconds(3600);
        String expiredToken = io.jsonwebtoken.Jwts.builder()
                .subject("user-a")
                .claim("role", "USER")
                .claim("tenantCode", "demo-a")
                .issuedAt(Date.from(past.minusSeconds(60)))
                .expiration(Date.from(past))
                .signWith(Keys.hmacShaKeyFor(SECRET.getBytes(StandardCharsets.UTF_8)))
                .compact();

        assertThatThrownBy(() -> shortLivedIssuer.parseClaims(expiredToken))
                .isInstanceOf(ExpiredJwtException.class);
    }

    @Test
    void parseClaims_shouldThrowWhenTokenTampered() {
        String token = jwtService.issue("user-a", "USER", "demo-a");
        String tampered = token.substring(0, token.length() - 1) + (token.endsWith("a") ? "b" : "a");

        assertThatThrownBy(() -> jwtService.parseClaims(tampered))
                .isInstanceOf(SignatureException.class);
    }

    @Test
    void parseClaims_shouldThrowWhenSignedWithDifferentSecret() {
        JwtService otherIssuer = new JwtService("a-completely-different-secret-key-0123456789", 24);
        String token = otherIssuer.issue("user-a", "USER", "demo-a");

        assertThatThrownBy(() -> jwtService.parseClaims(token))
                .isInstanceOf(JwtException.class);
    }
}
