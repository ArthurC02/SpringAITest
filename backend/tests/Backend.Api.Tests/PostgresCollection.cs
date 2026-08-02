namespace Backend.Api.Tests;

/// <summary>
/// 所有使用真實 appdb 的測試共用 fixture 並序列執行，避免初始化 DDL 互相鎖定。
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
