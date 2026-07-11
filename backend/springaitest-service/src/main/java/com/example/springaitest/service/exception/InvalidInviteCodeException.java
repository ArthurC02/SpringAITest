package com.example.springaitest.service.exception;

/**
 * 註冊時提供的租戶邀請碼與租戶不符（防止匿名者任意加入租戶）。
 */
public class InvalidInviteCodeException extends RuntimeException {

    public InvalidInviteCodeException(String message) {
        super(message);
    }
}
