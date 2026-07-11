package com.example.springaitest.service.exception;

/**
 * 註冊時使用者名稱已被使用。
 */
public class UsernameTakenException extends RuntimeException {

    public UsernameTakenException(String message) {
        super(message);
    }
}
