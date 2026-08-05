using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.Analysis;
using Backend.Api.Auth;
using Backend.Api.BusinessWorkflows;
using Backend.Api.Common;
using Backend.Api.Config;
using Backend.Api.Configuration;
using Backend.Api.Conversations;
using Backend.Api.Data;
using Backend.Api.Data.InMemory;
using Backend.Api.Files;
using Backend.Api.Skills;
using Backend.Api.Workflows;
using Backend.Api.Orchestrators;
using Backend.Api.OrchestratorRuns;
using Backend.Api.RuntimeDiscovery;
using Backend.Api.OperationsGovernance;
using Backend.Api.Contexts;
using Backend.Api.PromptArtifacts;
using Backend.Api.CheckpointRetention;
using Microsoft.AspNetCore.Mvc;

// 專用 migration 行程:與一般啟動完全分離的分支,在建 host 之前就結束。
// DbMigrationRunner 只在這裡被呼叫 —— 正常啟動流程(下方 DbBootstrap.RunAsync)永遠不碰它,
// 也永遠不讀 ALLOW_DESTRUCTIVE_MIGRATION(02-spec §6、§6.1)。
if (args.Length > 0 && args[0] == Backend.Api.Data.Migrations.MigrationCommand.CommandName)
{
    Environment.Exit(await Backend.Api.Data.Migrations.MigrationCommand.RunAsync(args));
}

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------------------------------------------------------------------------
// 組態:一律讀 flat 環境變數(env var 會自動進 IConfiguration)。
// ---------------------------------------------------------------------------
var connString = cfg["DB_CONNECTION_STRING"]
    ?? StartupCredentialValidator.DevelopmentDatabaseConnectionString;
var internalToken = InternalTokenResolver.Resolve(cfg["INTERNAL_API_TOKEN"]);
var jwtSigning = JwtSigningConfiguration.Resolve(
    cfg["JWT_ISSUER"],
    cfg["JWT_AUDIENCE"],
    cfg["JWT_ACTIVE_KID"],
    cfg["JWT_PRIVATE_KEY_PEM_BASE64"]);
var embeddingsProvider = cfg["EMBEDDINGS_PROVIDER"] ?? "fake";
var llmBaseUrl = cfg["LLM_BASE_URL"] ?? "http://localhost:4000";
var litellmKey = cfg["LITELLM_KEY"] ?? "sk-1234";
var embeddingModel = cfg["EMBEDDING_MODEL"] ?? "text-embedding-3-small";
var rabbitUrl = cfg["RABBITMQ_URL"] ?? StartupCredentialValidator.DevelopmentRabbitMqUrl;
var workflowBaseUrl = cfg["WORKFLOW_BASE_URL"] ?? "http://localhost:8001";
var useInMemoryDb = string.Equals(cfg["DB_PROVIDER"], "inmemory", StringComparison.OrdinalIgnoreCase);
StartupCredentialValidator.Validate(builder.Environment.EnvironmentName, connString, internalToken, jwtSigning, rabbitUrl);
builder.Services.AddSingleton(new ConversationCursorCodec(internalToken));
builder.Services.AddSingleton(new CheckpointRetentionCodec(internalToken));
var workflowDesignerEnabled = string.Equals(cfg["WORKFLOW_DESIGNER_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
// Runtime kill switch, self-contained (02-spec §8): WORKFLOW_DESIGNER_ENABLED stays an
// administration gate for the authoring routes only and is explicitly removed from runtime
// readiness -- turning the designer off must not stop dispatch. Chat and context enrichment
// still depend on dispatch; those two layers are the spec's.
var multiAgentDispatchEnabled = string.Equals(cfg["MULTI_AGENT_DISPATCH_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
var agentChatEnabled = multiAgentDispatchEnabled
    && string.Equals(cfg["AGENT_CHAT_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
var agentWriteToolsEnabled = string.Equals(cfg["AGENT_WRITE_TOOLS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
var contextEnrichmentEnabled = multiAgentDispatchEnabled
    && string.Equals(cfg["CONTEXT_ENRICHMENT_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(new ContextEnrichmentState(contextEnrichmentEnabled));
// E1: independently fail-closed. Off just stops new envelope writes; it neither depends on nor
// gates any other flag, and never hides an existing route (see plan §8).
var runEvidenceEnabled = string.Equals(cfg["RUN_EVIDENCE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(new RunEvidenceState(runEvidenceEnabled));
// E2/E3: independently fail-closed, same posture as RUN_EVIDENCE_ENABLED -- off only hides the new
// eval-suite/eval-run routes and stops new eval runs; it never depends on or gates another flag,
// and never rewrites the pre-existing caller-supplied `passed` regression contract (plan §8).
// Gating itself is the middleware closure below (runEvalEnabled), not a DI-injected state type.
var runEvalEnabled = string.Equals(cfg["RUN_EVAL_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
// P1: independently fail-closed. Off hides the prompt component/manifest routes and makes Agent
// publish ignore `prompt_manifest_revision`, leaving the publish path byte-for-byte unchanged.
var promptArtifactsEnabled = string.Equals(cfg["PROMPT_ARTIFACTS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(new PromptArtifactsState(promptArtifactsEnabled));
builder.Services.AddSingleton(sp => new BackendReadinessProbe(
    databaseRequired: !useInMemoryDb && !builder.Environment.IsEnvironment("Testing"),
    sp.GetService<Npgsql.NpgsqlDataSource>()));

// ---------------------------------------------------------------------------
// 資料層:預設 NpgsqlDataSource singleton + Dapper 儲存庫(薄介面,測試可換 fake)。
// DB_PROVIDER=inmemory(Lite 模式):六個行程記憶體儲存庫,注為 singleton
// (scoped 會每請求重建 → 什麼都存不住),且**不**建 NpgsqlDataSource(不連 DB)。
// ---------------------------------------------------------------------------
if (useInMemoryDb)
{
    builder.Services.AddSingleton<IAuthRepository, InMemoryAuthRepository>();
    builder.Services.AddSingleton<IConversationRepository, InMemoryConversationRepository>();
    builder.Services.AddSingleton<InMemoryRagRepository>();
    builder.Services.AddSingleton<IRagRepository>(sp => sp.GetRequiredService<InMemoryRagRepository>());
    builder.Services.AddSingleton<IConfigRepository, InMemoryConfigRepository>();
    builder.Services.AddSingleton<ISkillRepository, InMemorySkillRepository>();
    builder.Services.AddSingleton<IConfigurationSetRepository, InMemoryConfigurationSetRepository>();
    builder.Services.AddSingleton<IAgentRepository, InMemoryAgentRepository>();
    builder.Services.AddSingleton<IAgentRunRepository, InMemoryAgentRunRepository>();
    builder.Services.AddSingleton<IAgentRunApprovalRepository, InMemoryAgentRunApprovalRepository>();
    builder.Services.AddSingleton<IWorkflowRepository, InMemoryWorkflowRepository>();
    builder.Services.AddSingleton<IOrchestratorRepository, InMemoryOrchestratorRepository>();
    builder.Services.AddSingleton<IOrchestratorRunRepository, InMemoryOrchestratorRunRepository>();
    builder.Services.AddSingleton<IRuntimeBindingRepository, InMemoryRuntimeBindingRepository>();
    builder.Services.AddSingleton<IOperationsGovernanceRepository, InMemoryOperationsGovernanceRepository>();
    builder.Services.AddSingleton<IEvalRepository, InMemoryEvalRepository>();
    builder.Services.AddSingleton<IContextRepository, InMemoryContextRepository>();
    builder.Services.AddSingleton<IPromptArtifactRepository, InMemoryPromptArtifactRepository>();
    builder.Services.AddSingleton<ICheckpointRetentionRepository, InMemoryCheckpointRetentionRepository>();
}
else
{
    builder.Services.AddNpgsqlDataSource(connString);
    builder.Services.AddScoped<IAuthRepository, AuthRepository>();
    builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
    builder.Services.AddScoped<IRagRepository, RagRepository>();
    builder.Services.AddScoped<IConfigRepository, ConfigRepository>();
    builder.Services.AddScoped<ISkillRepository, SkillRepository>();
    builder.Services.AddScoped<IConfigurationSetRepository, ConfigurationSetRepository>();
    builder.Services.AddScoped<IAgentRepository, AgentRepository>();
    builder.Services.AddScoped<IAgentRunRepository, AgentRunRepository>();
    builder.Services.AddScoped<IAgentRunApprovalRepository, AgentRunApprovalRepository>();
    builder.Services.AddScoped<IWorkflowRepository, WorkflowRepository>();
    builder.Services.AddScoped<IOrchestratorRepository, OrchestratorRepository>();
    builder.Services.AddScoped<IOrchestratorRunRepository, OrchestratorRunRepository>();
    builder.Services.AddScoped<ICheckpointRetentionRepository, CheckpointRetentionRepository>();
    builder.Services.AddScoped<IRuntimeBindingRepository, RuntimeBindingRepository>();
    builder.Services.AddScoped<IOperationsGovernanceRepository, OperationsGovernanceRepository>();
    builder.Services.AddScoped<IEvalRepository, EvalRepository>();
    builder.Services.AddScoped<IContextRepository, ContextRepository>();
    builder.Services.AddScoped<IPromptArtifactRepository, PromptArtifactRepository>();
}

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<IPasswordVerifier>(BCryptPasswordVerifier.Instance);
builder.Services.AddSingleton(new JwtService(jwtSigning, TimeSpan.FromHours(24)));

// Skill 定義的靜態驗證:唯一事實來源是 workflow 引擎(:8001)。backend → workflow 的反向依賴為
// 設計上的取捨(03-design §4.1):validate 無副作用、失敗即快速回 502/422,不把驗證規則複製到 backend。
builder.Services.AddHttpClient("skill-validator", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<ISkillValidator>(sp => new WorkflowSkillValidator(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("skill-validator"), workflowBaseUrl, internalToken));

// Agent Business Rule AST 的語意/型別/安全限制同樣只由 Workflow 擁有。Agent validate 與 publish
// 都經這個 request-time dependency；引擎不可達一律 502 且不得發布。
builder.Services.AddHttpClient("business-rule-validator", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<IBusinessRuleValidator>(sp => new WorkflowBusinessRuleValidator(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("business-rule-validator"),
    workflowBaseUrl,
    internalToken));
builder.Services.AddScoped<RuntimeDiscoveryService>();
builder.Services.AddHttpClient("workflow-designer", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IWorkflowCompiler>(sp => new WorkflowDesignerCompiler(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("workflow-designer"), workflowBaseUrl, internalToken, sp.GetRequiredService<IHttpContextAccessor>()));

// Agent Skill package 驗證(P0):multipart 轉送 zip 給引擎 POST /skills/validate-package(唯一結構/語意權威)。
builder.Services.AddHttpClient("skill-package-validator", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<ISkillPackageValidator>(sp => new WorkflowSkillPackageValidator(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("skill-package-validator"), workflowBaseUrl, internalToken));

// E2 eval runner:呼叫引擎(:8001)的 POST /evals/run(workflow-internal、無 /api prefix)。
// 引擎 flag 關閉或不可達皆為 502(比照 skill validate 的 request-time dependency 慣例)。
builder.Services.AddHttpClient("eval-runner", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddScoped<IEvalRunner>(sp => new WorkflowEvalRunner(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("eval-runner"), workflowBaseUrl, internalToken));

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

// 啟動時建表 + 種子(Testing 環境或 DB_PROVIDER=inmemory 時跳過:兩者皆不連真 DB)。
if (!app.Environment.IsEnvironment("Testing") && !useInMemoryDb)
{
    var dataSource = app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    await DbBootstrap.RunAsync(dataSource, logger);
}

app.UseExceptionHandler();

// 內部憑證守門(只有明確 health 端點免驗);置於例外處理之後、路由之前。
if (!agentWriteToolsEnabled)
{
    app.Use(async (context, next) =>
    {
        var d7RunPath = context.Request.Path.StartsWithSegments("/api/agent-runs")
            && (context.Request.Path.Value?.Contains("/approvals", StringComparison.OrdinalIgnoreCase) == true
                || context.Request.Path.Value?.Contains("/write-effects", StringComparison.OrdinalIgnoreCase) == true);
        if (((context.Request.Path.StartsWithSegments("/api/runs") || context.Request.Path.StartsWithSegments("/api/agent-runs")) && context.Request.Path.Value?.Contains("/approvals", StringComparison.OrdinalIgnoreCase) == true)
            || d7RunPath
            || context.Request.Path.StartsWithSegments("/api/agent-run-approval-executions")
            || context.Request.Path.StartsWithSegments("/api/admin/operations")
            || context.Request.Path.StartsWithSegments("/api/operations/telemetry"))
        { await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status404NotFound, "Feature is unavailable"); return; }
        await next();
    });
}

app.UseMiddleware<InternalTokenMiddleware>(internalToken);

// D4 authoring endpoints stay invisible even to a direct internal caller while rollout is off.
if (!workflowDesignerEnabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/admin/workflows")
            || context.Request.Path.StartsWithSegments("/api/admin/orchestrators"))
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status404NotFound, "Feature not enabled");
            return;
        }
        await next();
    });
}

// D5 is independently undiscoverable until the multi-agent dispatch rollout is explicitly
// enabled.  This is deliberately before MVC/auth, matching the D3/D4 posture.  The Designer
// gate above is administration-only and no longer participates (02-spec §8).
if (!multiAgentDispatchEnabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/orchestrator-runs")
            || context.Request.Path.StartsWithSegments("/api/admin/orchestrators")
               && context.Request.Path.Value?.Contains("/runs", StringComparison.OrdinalIgnoreCase) == true)
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status404NotFound, "Feature is unavailable");
            return;
        }
        await next();
    });
}

// Context APIs are internal implementation details of D5/D6.  The acquire route remains
// available under D5 so an off flag preserves its established fail-closed not-ready response.
if (!contextEnrichmentEnabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/contexts")
            || context.Request.Path.StartsWithSegments("/api/context-views")
            || context.Request.Path.StartsWithSegments("/api/context-policies")
            || context.Request.Path.StartsWithSegments("/api/orchestrator-runs")
               && context.Request.Path.Value?.Contains("/context-requests", StringComparison.OrdinalIgnoreCase) == true)
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status404NotFound, "Feature is unavailable");
            return;
        }
        await next();
    });
}

if (!agentChatEnabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/runtime-discovery")
            || context.Request.Path.StartsWithSegments("/api/chat-runs")
            || context.Request.Path.StartsWithSegments("/api/admin/runtime-binding"))
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status404NotFound, "Feature is unavailable");
            return;
        }
        await next();
    });
}

// E2/E3 eval-suite/eval-run routes stay invisible while off, independently of every other flag
// (including AGENT_WRITE_TOOLS_ENABLED, which separately gates the whole /api/admin/operations
// prefix these routes also live under -- both must be on for the routes to be reachable).
if (!runEvalEnabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/admin/operations/eval-suites")
            || context.Request.Path.StartsWithSegments("/api/admin/operations/eval-runs"))
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status404NotFound, "Feature is unavailable");
            return;
        }
        await next();
    });
}

// P1 prompt artifact routes stay invisible while off, independently of every other flag. Publish
// pinning is gated separately inside AgentController (the publish route itself never disappears).
if (!promptArtifactsEnabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/prompt-components")
            || context.Request.Path.StartsWithSegments("/api/prompt-manifests"))
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status404NotFound, "Feature is unavailable");
            return;
        }
        await next();
    });
}

app.MapControllers();

// 公開 health 端點。舊 /health 明確保留為 liveness alias。
app.MapGet("/health/live", BackendHealthEndpoints.Live);
app.MapGet("/health/ready", BackendHealthEndpoints.Ready);
app.MapGet("/health", BackendHealthEndpoints.Live);

app.Run();

// 讓 WebApplicationFactory<Program> 測試能引用進入點。
public partial class Program;
