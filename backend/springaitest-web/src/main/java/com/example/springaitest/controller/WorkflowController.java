package com.example.springaitest.controller;

import com.example.springaitest.security.AuthenticatedUser;
import com.example.springaitest.service.WorkflowService;
import com.example.springaitest.service.dto.UserContext;
import com.example.springaitest.service.dto.WorkflowInfo;
import com.example.springaitest.service.dto.WorkflowInvokeRequest;
import com.example.springaitest.service.dto.WorkflowInvokeResponse;
import jakarta.validation.Valid;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.List;

/**
 * 展示層：工作流相關的對外 REST API 進入點。
 * 只負責接收請求 / 組出呼叫者身分 / 回傳結果，實際的 HTTP 觸發邏輯一律委派給 Service。
 */
@RestController
@RequestMapping("/api/workflows")
public class WorkflowController {

    private final WorkflowService workflowService;

    public WorkflowController(WorkflowService workflowService) {
        this.workflowService = workflowService;
    }

    /** 取得工作流服務目前註冊的所有工作流。 */
    @GetMapping
    public List<WorkflowInfo> list(@AuthenticationPrincipal AuthenticatedUser user) {
        return workflowService.list(toUserContext(user));
    }

    /** 觸發指定名稱的工作流，同步取得執行結果。 */
    @PostMapping("/{name}")
    public WorkflowInvokeResponse invoke(@PathVariable String name,
                                         @Valid @RequestBody WorkflowInvokeRequest request,
                                         @AuthenticationPrincipal AuthenticatedUser user) {
        return workflowService.invoke(name, request.input(), toUserContext(user));
    }

    private UserContext toUserContext(AuthenticatedUser user) {
        return new UserContext(user.username(), user.tenantCode(), user.role());
    }
}
