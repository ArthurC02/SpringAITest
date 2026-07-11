package com.example.springaitest.exception;

import com.example.springaitest.service.exception.DocumentNotFoundException;
import com.example.springaitest.service.exception.InvalidCredentialsException;
import com.example.springaitest.service.exception.InvalidInviteCodeException;
import com.example.springaitest.service.exception.TenantNotFoundException;
import com.example.springaitest.service.exception.UsernameTakenException;
import com.example.springaitest.service.exception.WorkflowBadInputException;
import com.example.springaitest.service.exception.WorkflowForbiddenException;
import com.example.springaitest.service.exception.WorkflowInvocationException;
import com.example.springaitest.service.exception.WorkflowNotFoundException;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.validation.FieldError;
import org.springframework.web.bind.MethodArgumentNotValidException;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;

import java.time.Instant;
import java.util.HashMap;
import java.util.Map;

/**
 * 展示層的橫切關注點：集中處理例外，轉成統一的 ApiError 格式。
 */
@RestControllerAdvice
public class GlobalExceptionHandler {

    /** 處理 @Valid 驗證失敗。 */
    @ExceptionHandler(MethodArgumentNotValidException.class)
    public ResponseEntity<ApiError> handleValidation(MethodArgumentNotValidException ex) {
        Map<String, String> fieldErrors = new HashMap<>();
        for (FieldError error : ex.getBindingResult().getFieldErrors()) {
            fieldErrors.put(error.getField(), error.getDefaultMessage());
        }
        ApiError body = new ApiError(
                Instant.now(),
                HttpStatus.BAD_REQUEST.value(),
                "輸入驗證失敗",
                fieldErrors);
        return ResponseEntity.badRequest().body(body);
    }

    /** 找不到指定名稱的工作流（工作流服務回傳 404）。 */
    @ExceptionHandler(WorkflowNotFoundException.class)
    public ResponseEntity<ApiError> handleWorkflowNotFound(WorkflowNotFoundException ex) {
        ApiError body = new ApiError(
                Instant.now(),
                HttpStatus.NOT_FOUND.value(),
                ex.getMessage(),
                Map.of());
        return ResponseEntity.status(HttpStatus.NOT_FOUND).body(body);
    }

    /** 呼叫工作流服務失敗：以 502 區隔「上游工作流服務故障」與自身的 500。 */
    @ExceptionHandler(WorkflowInvocationException.class)
    public ResponseEntity<ApiError> handleWorkflowInvocation(WorkflowInvocationException ex) {
        ApiError body = new ApiError(
                Instant.now(),
                HttpStatus.BAD_GATEWAY.value(),
                ex.getMessage(),
                Map.of());
        return ResponseEntity.status(HttpStatus.BAD_GATEWAY).body(body);
    }

    /** 找不到指定代碼的租戶（例如註冊時 tenantCode 查無對應租戶）。 */
    @ExceptionHandler(TenantNotFoundException.class)
    public ResponseEntity<ApiError> handleTenantNotFound(TenantNotFoundException ex) {
        return buildError(HttpStatus.NOT_FOUND, ex.getMessage());
    }

    /** 註冊時使用者名稱已被使用。 */
    @ExceptionHandler(UsernameTakenException.class)
    public ResponseEntity<ApiError> handleUsernameTaken(UsernameTakenException ex) {
        return buildError(HttpStatus.CONFLICT, ex.getMessage());
    }

    /** 註冊時邀請碼與租戶不符：不可加入該租戶。 */
    @ExceptionHandler(InvalidInviteCodeException.class)
    public ResponseEntity<ApiError> handleInvalidInviteCode(InvalidInviteCodeException ex) {
        return buildError(HttpStatus.FORBIDDEN, ex.getMessage());
    }

    /** 登入時帳號或密碼錯誤。 */
    @ExceptionHandler(InvalidCredentialsException.class)
    public ResponseEntity<ApiError> handleInvalidCredentials(InvalidCredentialsException ex) {
        return buildError(HttpStatus.UNAUTHORIZED, ex.getMessage());
    }

    /** 呼叫工作流時角色權限不足（工作流服務回傳 403）。 */
    @ExceptionHandler(WorkflowForbiddenException.class)
    public ResponseEntity<ApiError> handleWorkflowForbidden(WorkflowForbiddenException ex) {
        return buildError(HttpStatus.FORBIDDEN, ex.getMessage());
    }

    /** 呼叫工作流時輸入不符合下游 schema（工作流服務回傳 422）。 */
    @ExceptionHandler(WorkflowBadInputException.class)
    public ResponseEntity<ApiError> handleWorkflowBadInput(WorkflowBadInputException ex) {
        return buildError(HttpStatus.BAD_REQUEST, ex.getMessage());
    }

    /** 找不到指定 id 的文件（文件服務回傳 404）。 */
    @ExceptionHandler(DocumentNotFoundException.class)
    public ResponseEntity<ApiError> handleDocumentNotFound(DocumentNotFoundException ex) {
        return buildError(HttpStatus.NOT_FOUND, ex.getMessage());
    }

    /** 兜底：其他未預期的例外。 */
    @ExceptionHandler(Exception.class)
    public ResponseEntity<ApiError> handleGeneric(Exception ex) {
        ApiError body = new ApiError(
                Instant.now(),
                HttpStatus.INTERNAL_SERVER_ERROR.value(),
                ex.getMessage(),
                Map.of());
        return ResponseEntity.status(HttpStatus.INTERNAL_SERVER_ERROR).body(body);
    }

    /** 組出統一格式的錯誤回應，減少重複的 ApiError 組裝樣板。 */
    private ResponseEntity<ApiError> buildError(HttpStatus status, String message) {
        ApiError body = new ApiError(Instant.now(), status.value(), message, Map.of());
        return ResponseEntity.status(status).body(body);
    }
}
