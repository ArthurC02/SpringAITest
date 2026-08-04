using System.Text;
using Dapper;
using Npgsql;

namespace Backend.Api.Tests.Migrations;

/// <summary>
/// <c>SchemaFingerprint.sql</c> 的 canonical serializer。
/// 腳本負責「投影什麼、怎麼排序」(它才是驗收標準的權威定義);
/// 這裡只負責「怎麼把已排序的列變成一個可比較的字串」——每列四欄以 <c>|</c> 分隔、換行串接。
/// fresh-vs-reset 與 dump/restore 兩種比對共用同一份輸出。
/// </summary>
internal static class SchemaFingerprint
{
    private const string Resource = "Backend.Api.Tests.Migrations.SchemaFingerprint.sql";

    private static readonly string Query = ReadQuery();

    public static async Task<string> ComputeAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // 用 dynamic 讀:欄位名由腳本(驗收標準的權威定義)決定,不讓 C# 型別反過來綁住它。
        var rows = await conn.QueryAsync(Query);

        var builder = new StringBuilder();
        foreach (IDictionary<string, object> row in rows)
        {
            builder.Append(row["object_schema"]).Append('|')
                .Append(row["object_type"]).Append('|')
                .Append(row["object_name"]).Append('|')
                .Append(row["detail"]).Append('\n');
        }

        return builder.ToString();
    }

    private static string ReadQuery()
    {
        using var stream = typeof(SchemaFingerprint).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"讀不到 fingerprint 腳本:{Resource}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
