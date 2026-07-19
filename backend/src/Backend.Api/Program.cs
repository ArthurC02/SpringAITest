using Backend.Api.Analysis;
using Backend.Api.Auth;
using Backend.Api.Common;
using Backend.Api.Config;
using Backend.Api.Configuration;
using Backend.Api.Conversations;
using Backend.Api.Data;
using Backend.Api.Files;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------------------------------------------------------------------------
// 組態:一律讀 flat 環境變數(env var 會自動進 IConfiguration)。
// ---------------------------------------------------------------------------
var connString = cfg["DB_CONNECTION_STRING"]
    ?? "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest";
var internalToken = InternalTokenResolver.Resolve(cfg["INTERNAL_API_TOKEN"]);
var jwtSecret = cfg["JWT_SECRET"] ?? "dev-jwt-secret-change-me-0123456789abcdef";
var embeddingsProvider = cfg["EMBEDDINGS_PROVIDER"] ?? "fake";
var llmBaseUrl = cfg["LLM_BASE_URL"] ?? "http://localhost:4000";
var litellmKey = cfg["LITELLM_KEY"] ?? "sk-1234";
var embeddingModel = cfg["EMBEDDING_MODEL"] ?? "text-embedding-3-small";
var rabbitUrl = cfg["RABBITMQ_URL"] ?? "amqp://app:app-dev-password@localhost:5672";
var workflowBaseUrl = cfg["WORKFLOW_BASE_URL"] ?? "http://localhost:8001";

// ---------------------------------------------------------------------------
// 資料層:NpgsqlDataSource singleton + Dapper 儲存庫(薄介面,測試可換 fake)。
// ---------------------------------------------------------------------------
builder.Services.AddNpgsqlDataSource(connString);
builder.Services.AddScoped<IAuthRepository, AuthRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<IRagRepository, RagRepository>();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();
builder.Services.AddScoped<ISkillRepository, SkillRepository>();
builder.Services.AddScoped<IConfigurationSetRepository, ConfigurationSetRepository>();

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton(new JwtService(jwtSecret, TimeSpan.FromHours(24)));

// Skill 定義的靜態驗證:唯一事實來源是 workflow 引擎(:8001)。backend → workflow 的反向依賴為
// 設計上的取捨(03-design §4.1):validate 無副作用、失敗即快速回 502/422,不把驗證規則複製到 backend。
builder.Services.AddHttpClient("skill-validator", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<ISkillValidator>(sp => new WorkflowSkillValidator(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("skill-validator"), workflowBaseUrl, internalToken));

// 嵌入 provider 由 EMBEDDINGS_PROVIDER 決定;預設 fake(確定性、免金鑰)。
if (string.Equals(embeddingsProvider, "openai", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient("embeddings");
    builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
        new OpenAiEmbeddingProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("embeddings"),
            llmBaseUrl, litellmKey, embeddingModel));
}
else
{
    // 與 Data/DbBootstrap 的 rag_chunks.embedding vector(1536) 綁定 — 改維度要一起改 DDL。
    const int EmbeddingDim = 1536;
    builder.Services.AddSingleton<IEmbeddingProvider>(new FakeEmbeddingProvider(EmbeddingDim));
}

// ---------------------------------------------------------------------------
// 文件非同步處理:DocumentProcessor(每則訊息一個 scope)+ RabbitMQ 消費者背景服務。
// Testing 環境不啟動消費者(不連 broker);消費者連線失敗會退避重試,不讓 host 崩潰。
// ---------------------------------------------------------------------------
builder.Services.AddScoped<DocumentProcessor>();
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService(sp => new DocumentConsumerService(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<DocumentConsumerService>>(),
        rabbitUrl));
}

// ---------------------------------------------------------------------------
// MVC + 驗證失敗回應(統一 ApiError)
// ---------------------------------------------------------------------------
builder.Services.AddControllers();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = ValidationErrorResponse.Create;
});

// ---------------------------------------------------------------------------
// 全域例外處理
// ---------------------------------------------------------------------------
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// 啟動時建表 + 種子(Testing 環境跳過:測試以 fake repository 取代,不連真 DB)。
if (!app.Environment.IsEnvironment("Testing"))
{
    var dataSource = app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    await DbBootstrap.RunAsync(dataSource, logger);
}

app.UseExceptionHandler();

// 內部憑證守門(/health 免驗);置於例外處理之後、路由之前。
app.UseMiddleware<InternalTokenMiddleware>(internalToken);

app.MapControllers();

// 健康檢查(公開、免 token)。
app.MapGet("/health", () => Results.Json(new { status = "UP" }));

app.Run();

// 讓 WebApplicationFactory<Program> 測試能引用進入點。
public partial class Program;
