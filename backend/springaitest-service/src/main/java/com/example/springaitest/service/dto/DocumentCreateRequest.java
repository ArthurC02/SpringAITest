package com.example.springaitest.service.dto;

import jakarta.validation.constraints.NotBlank;

/**
 * 業務層輸入 DTO：建立文件的請求內容。
 */
public record DocumentCreateRequest(

        @NotBlank(message = "title 不可為空")
        String title,

        @NotBlank(message = "text 不可為空")
        String text
) {
}
