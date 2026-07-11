package com.example.springaitest.service.exception;

/**
 * 登入時帳號或密碼錯誤。
 */
public class InvalidCredentialsException extends RuntimeException {

    public InvalidCredentialsException(String message) {
        super(message);
    }
}
