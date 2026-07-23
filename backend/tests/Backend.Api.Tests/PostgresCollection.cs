namespace Backend.Api.Tests;

/// <summary>
/// 所有打真 appdb 的 repository 測試(ConfigurationSet / Rag / Skill)共用同一個 PostgresFixture,
/// 並**序列化執行** — xUnit 同一 collection 內的 test class 不平行。這避免多個 class 各自併發跑
/// DbBootstrap 的 DDL(CREATE INDEX / ALTER / CREATE EXTENSION)在系統目錄上相互卡住而偶發失敗。
/// PostgresFixture 定義於 ConfigurationSetRepositoryTests.cs;此處只宣告共用 collection。
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
