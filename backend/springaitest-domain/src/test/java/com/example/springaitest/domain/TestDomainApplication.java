package com.example.springaitest.domain;

import org.springframework.boot.autoconfigure.SpringBootApplication;

/**
 * 僅供測試使用的最小啟動設定。
 * domain 模組本身沒有 @SpringBootApplication，@DataJpaTest 需要一個
 * @SpringBootConfiguration 作為掃描起點（entity 與 repository 都在本 package 之下）。
 */
@SpringBootApplication
class TestDomainApplication {
}
