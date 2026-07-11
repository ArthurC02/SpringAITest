package com.example.springaitest.controller;

import com.example.springaitest.security.AuthenticatedUser;
import com.example.springaitest.service.DocumentService;
import com.example.springaitest.service.dto.DocumentCreateRequest;
import com.example.springaitest.service.dto.DocumentCreated;
import com.example.springaitest.service.dto.DocumentInfo;
import com.example.springaitest.service.dto.UserContext;
import jakarta.validation.Valid;
import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import java.util.List;

/**
 * 展示層：文件相關的對外 REST API 進入點（代理至下游的文件／RAG 服務）。
 * 只負責接收請求 / 組出呼叫者身分 / 回傳結果，實際的 HTTP 代理邏輯一律委派給 Service。
 */
@RestController
@RequestMapping("/api/documents")
public class DocumentController {

    private final DocumentService documentService;

    public DocumentController(DocumentService documentService) {
        this.documentService = documentService;
    }

    /** 新增一份文件。 */
    @PostMapping
    @ResponseStatus(HttpStatus.CREATED)
    public DocumentCreated create(@Valid @RequestBody DocumentCreateRequest request,
                                   @AuthenticationPrincipal AuthenticatedUser user) {
        return documentService.create(request, toUserContext(user));
    }

    /** 取得目前所有文件的清單。 */
    @GetMapping
    public List<DocumentInfo> list(@AuthenticationPrincipal AuthenticatedUser user) {
        return documentService.list(toUserContext(user));
    }

    /** 刪除指定 id 的文件。 */
    @DeleteMapping("/{id}")
    @ResponseStatus(HttpStatus.NO_CONTENT)
    public void delete(@PathVariable String id, @AuthenticationPrincipal AuthenticatedUser user) {
        documentService.delete(id, toUserContext(user));
    }

    private UserContext toUserContext(AuthenticatedUser user) {
        return new UserContext(user.username(), user.tenantCode(), user.role());
    }
}
