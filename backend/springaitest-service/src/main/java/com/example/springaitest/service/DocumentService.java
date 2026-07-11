package com.example.springaitest.service;

import com.example.springaitest.service.dto.DocumentCreateRequest;
import com.example.springaitest.service.dto.DocumentCreated;
import com.example.springaitest.service.dto.DocumentInfo;
import com.example.springaitest.service.dto.UserContext;

import java.util.List;

/**
 * 業務層介面：定義文件相關的業務行為。
 * 實作透過 HTTP 代理至外部的 Python（工作流／RAG）服務。
 */
public interface DocumentService {

    /**
     * 新增一份文件（下游會切成 chunk 並建立索引）。
     *
     * @param request 文件標題與內容
     * @param ctx     呼叫者身分（租戶 / 使用者 / 角色），將轉為下游 header
     * @return 建立結果（含產生的 chunk 數）
     */
    DocumentCreated create(DocumentCreateRequest request, UserContext ctx);

    /**
     * 取得目前所有文件的清單。
     *
     * @param ctx 呼叫者身分
     * @return 文件清單
     */
    List<DocumentInfo> list(UserContext ctx);

    /**
     * 刪除指定 id 的文件。
     *
     * @param id  文件 id
     * @param ctx 呼叫者身分
     */
    void delete(String id, UserContext ctx);
}
