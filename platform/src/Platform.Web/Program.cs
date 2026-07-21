using System.ClientModel;
using System.Threading.RateLimiting;
using Platform.Service;
using Platform.Service.Abstractions;
using Platform.Service.Options;
using Platform.Web.Auth;
using Platform.Web.Errors;
using Platform.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------------------------------------------------------------------------
// Options:一律從 flat 環境變數/設定鍵組裝(名稱不可變;env var 會自動進 IConfiguration)。
// ---------------------------------------------------------------------------
var llmOptions = new LlmOptions
{
    BaseUrl = cfg["LLM_BASE_URL"] ?? "http://localhost:4000",
    ApiKey = cfg["LITELLM_KEY"] ?? "sk-1234",
    ChatModel = cfg["CHAT_MODEL"] ?? "gpt-4o-mini",
    // 0.2:偏低溫度讓小模型更傾向乖乖呼叫工具、少自由發揮心算(數字問答的可靠性優先於閒聊創意)。
    // ponytail: 之後若要讓閒聊更活潑可改回讀 env,現在單一用途不需要旋鈕。
    Temperature = 0.2f,
};
var mem0Options = new Mem0Options
{
    BaseUrl = cfg["MEM0_BASE_URL"] ?? "http://localhost:8000",
};
var workflowOptions = new WorkflowOptions
{
    BaseUrl = cfg["WORKFLOW_BASE_URL"] ?? "http://localhost:8001",
    InternalToken = cfg["INTERNAL_API_TOKEN"] ?? "internal-dev-token",
};
var backendOptions = new BackendOptions
{
    BaseUrl = cfg["BACKEND_BASE_URL"] ?? "http://localhost:8002",
    InternalToken = cfg["INTERNAL_API_TOKEN"] ?? "internal-dev-token",
};
var rabbitMqOptions = new RabbitMqOptions
{
    Url = cfg["RABBITMQ_URL"] ?? "amqp://app:app-dev-password@localhost:5672",
};
var jwtOptions = new JwtOptions
{
    Secret = cfg["JWT_SECRET"] ?? "dev-jwt-secret-change-me-0123456789abcdef",
};

builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton(mem0Options);
builder.Services.AddSingleton(workflowOptions);
builder.Services.AddSingleton(backendOptions);
builder.Services.AddSingleton(rabbitMqOptions);
builder.Services.AddSingleton(jwtOptions);

// ---------------------------------------------------------------------------
// backend client:核心商業邏輯已抽到 backend(:8002)。連線逾時 5s;讀取逾時 90s
// (容納文件嵌入這類較慢的呼叫);強制 HTTP/1.1 在 BackendClient 內設定。
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient<BackendClient>(c => c.Timeout = TimeSpan.FromSeconds(90))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) });

// ---------------------------------------------------------------------------
// Services(認證/聊天歷史/文件/分析/組態 皆代理 backend;工作流仍打 Python :8001)
// ---------------------------------------------------------------------------
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IConversationStore, ConversationStore>();
builder.Services.AddScoped<IDocumentService, DocumentService>();

// 文件處理訊息發佈者:lazy 單例連線,執行緒安全,關閉時優雅釋放。
builder.Services.AddSingleton<IDocumentQueue, RabbitDocumentQueue>();
builder.Services.AddScoped<IAnalysisService, AnalysisService>();
builder.Services.AddScoped<IConfigService, ConfigService>();
builder.Services.AddScoped<ISkillService, SkillService>();
builder.Services.AddScoped<IConfigurationSetService, ConfigurationSetService>();

// 供 agent pipeline(P1+)取得本次請求的登入身分與記憶 key;Platform.Service 不能引用 ASP.NET Core。
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IChatIdentityAccessor, HttpChatIdentityAccessor>();

// LLM 代理:Agent Framework 實作,無狀態、單例即可。
builder.Services.AddSingleton<ILlmAgent>(_ => new AgentFrameworkLlmAgent(llmOptions));

// ---------------------------------------------------------------------------
// AG-UI(CopilotKit 操作助理):同一套 LiteLLM 佈線。IChatClient 註冊為單例,
// 讓 AG-UI 端點的 AIAgent 由它建立;測試可替換為 fake IChatClient 免打真 LLM。
// 系統提示定位為操作本平台的助理;實際操作工具由前端以 AG-UI client tools 提供,
// 助理只需正常回答並在需要時呼叫收到的工具。
// ---------------------------------------------------------------------------
const string copilotInstructions =
    "你是本系統的操作助理,協助使用者操作這個 AI 資料檢索與分析平台:" +
    "查詢與管理文件、執行工作流(例如檢索式問答)、查看分析摘要、切換視圖。" +
    "請一律以繁體中文回答,簡潔專業。當使用者的請求需要實際操作時," +
    "呼叫前端提供的工具(client tools)來完成;你只需正常回答並在需要時呼叫收到的工具。";

builder.Services.AddSingleton<IChatClient>(_ =>
    new OpenAIClient(
        new ApiKeyCredential(llmOptions.ApiKey),
        new OpenAIClientOptions
        {
            Endpoint = new Uri(llmOptions.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(90),
        })
    .GetChatClient(llmOptions.ChatModel)
    .AsIChatClient());
builder.Services.AddAGUI();

// 租戶隔離:isolation key 取自 JWT 身分(租戶:使用者),不得取自 threadId/body 任何欄位;
// Strict=true(框架預設)fail-closed——取不到身分時 session store 直接拋例外,不退回全域命名空間。
builder.Services.AddSingleton<SessionIsolationKeyProvider, JwtTenantIsolationKeyProvider>();

// P2(copilot-shared-core §3.3):兩條鏈路共用同一份短期記憶語意——20 則視窗,MessagesExceed(20) 觸發,
// minimumPreservedTurns 設 1(硬下限,設 20 時 26 則完全不觸發,spike 實測)。此物件本身無狀態
// (視窗內容存在 AgentSession.StateBag),兩顆 hosted agent 安全共用同一份設定。
var chatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = new SlidingWindowCompactionStrategy(
            trigger: CompactionTriggers.MessagesExceed(20),
            minimumPreservedTurns: 1)
        .AsChatReducer(),
});
builder.Services.AddSingleton(chatHistoryProvider);

// hosted agent:AG-UI 端點經此解析,由框架管理 session(GetOrCreateSession/SaveSession)。
// Singleton:MapAGUI 在啟動期從 root provider 一次性解析 agent(反編譯實證),宣告 Scoped 會讓
// Development(ValidateScopes=true)的 dotnet run 啟動即炸;per-request 身分不靠 agent 生命週期,
// 而是經 IChatIdentityAccessor 接線(底層 IHttpContextAccessor 是 AsyncLocal,單例持有仍每次讀到本請求
// 身分)。ChatContextProvider(護欄+mem0 recall→Instructions)與 ChatTurnRecorder(mem0 remember+持久化)
// 都只持 IServiceScopeFactory,每次呼叫時開新 scope 解析 Scoped 服務(IMem0Client/IConversationStore/
// IChatIdentityAccessor),不在建構時捕捉——避免兩顆 Singleton hosted agent 產生 captive dependency
// (copilot-shared-core P3 §11 步驟 11.5)。
// AguiWireDedupAgent(P2,B-P2-04):真實 @ag-ui/client 每輪重送完整 messages 陣列,疊上
// ChatHistoryProvider 的預設合併語意(session 歷史 + wire 訊息整段串接,框架不去重)會讓訊息數
// 隨輪次複合暴增。只包這顆 agent(鏈路 A 的 ChatAssistant 不受影響),見該類別 XML doc。
// pipeline(外→內):AguiWireDedupAgent → ChatTurnRecorder → SkillRoutingAgent → ChatClientAgent
// (掛 ChatContextProvider)。recorder 掛在 dedup 之內、routing 之外——dedup 先把 wire 重送的完整
// 陣列濾成「真正新訊息」,recorder 才看得到本輪唯一的新使用者訊息與完整回覆(而非累積的整段歷史);
// routing 掛在 recorder 之內、ChatClientAgent 之外,讓路由命中而短路的那一輪同樣會被 recorder 記住
// (P4,copilot-shared-core 03-design.md §3.3/§4.2)。
var copilotAgent = builder.Services.AddAIAgent(
        "OperationsAssistant",
        (IServiceProvider sp, string name) =>
        {
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var chatClientAgent = sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
            {
                Name = name,
                ChatOptions = new ChatOptions { Instructions = copilotInstructions },
                ChatHistoryProvider = chatHistoryProvider,
                AIContextProviders = new AIContextProvider[]
                {
                    new ChatContextProvider(scopeFactory, loggerFactory.CreateLogger<ChatContextProvider>()),
                },
            });
            var routing = new SkillRoutingAgent(
                chatClientAgent, sp.GetRequiredService<ILlmAgent>(), chatHistoryProvider, scopeFactory,
                loggerFactory.CreateLogger<SkillRoutingAgent>());
            var recorder = new ChatTurnRecorder(
                routing, scopeFactory, loggerFactory.CreateLogger<ChatTurnRecorder>());
            return new AguiWireDedupAgent(recorder, chatHistoryProvider);
        },
        ServiceLifetime.Singleton)
    .WithInMemorySessionStore(withIsolation: true);

// 鏈路 A(ChatController → ChatService)共用同一套 session store 類型/compaction 語意,但 persona 不同
// ——不得吃到副駕的 copilotInstructions(僅副駕,02-spec §3.1 拓樸注記),故另注一顆無 instructions 的
// named agent。AddAIAgent 只把 (AIAgent, AgentSessionStore) 註冊成 keyed service,並不會自動組成
// AIHostAgent——手動組裝一顆供 ChatService 使用(反編譯實證)。
// withIsolation:false(刻意與 AG-UI 不同)——ChatService 傳入的 conversationId(cid)已由
// DeriveMemoryKeys 保證跨租戶/跨使用者不撞(登入時含租戶:使用者前綴),不像 AG-UI 的 wire threadId
// 完全不帶身分安全語意。疊上 IsolationKeyScopedAgentSessionStore(Strict=true)只會讓匿名對談
// fail-closed,是對現行「匿名短期記憶仍可延續」行為的回歸(ChatServiceTests.
// Chat_ShortTermMemory_CarriesPriorExchange 等既有測試皆假設匿名也有連續性)。
// pipeline:ChatTurnRecorder → SkillRoutingAgent → ChatClientAgent(掛 ChatContextProvider)——鏈路 A
// 沒有 AguiWireDedupAgent 那一層(ChatService 每輪只送本輪新訊息,不會重送完整陣列)。
builder.Services.AddAIAgent(
        "ChatAssistant",
        (IServiceProvider sp, string name) =>
        {
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var chatClientAgent = sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
            {
                Name = name,
                ChatHistoryProvider = chatHistoryProvider,
                AIContextProviders = new AIContextProvider[]
                {
                    new ChatContextProvider(scopeFactory, loggerFactory.CreateLogger<ChatContextProvider>()),
                },
            });
            var routing = new SkillRoutingAgent(
                chatClientAgent, sp.GetRequiredService<ILlmAgent>(), chatHistoryProvider, scopeFactory,
                loggerFactory.CreateLogger<SkillRoutingAgent>());
            return new ChatTurnRecorder(
                routing, scopeFactory, loggerFactory.CreateLogger<ChatTurnRecorder>());
        },
        ServiceLifetime.Singleton)
    .WithInMemorySessionStore(withIsolation: false);
builder.Services.AddSingleton(sp => new AIHostAgent(
    sp.GetRequiredKeyedService<AIAgent>("ChatAssistant"),
    sp.GetRequiredKeyedService<AgentSessionStore>("ChatAssistant")));

// mem0:預設逾時即可(記憶best-effort)。
builder.Services.AddHttpClient<IMem0Client, Mem0Client>();

// 下游工作流 client:連線逾時 5s;讀取逾時 150s(強制 HTTP/1.1 在 service 內設定)。
builder.Services.AddHttpClient<IWorkflowService, WorkflowService>(c => c.Timeout = TimeSpan.FromSeconds(150))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) });

// ---------------------------------------------------------------------------
// MVC + 驗證失敗回應(統一 ApiError)
// ---------------------------------------------------------------------------
builder.Services.AddControllers();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = ValidationErrorResponse.Create;
});

// ---------------------------------------------------------------------------
// 認證/授權:JwtBearer(HS256、不驗 issuer/audience、ClockSkew=0、MapInboundClaims=false)
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = JwtService.BuildValidationParameters(jwtOptions.Secret);
        options.Events = new JwtBearerEvents
        {
            // 未認證/憑證無效:攔掉預設回應,改寫 ApiError 401。
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status401Unauthorized, "未認證或憑證無效");
            },
            // 已認證但被拒:改寫 ApiError 403。
            OnForbidden = async context =>
            {
                await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status403Forbidden, "權限不足");
            },
        };
    });
builder.Services.AddAuthorization();

// nginx 只追加一個 forwarding hop；限制為單一 hop並要求 X-Forwarded-For / Proto 對稱。
// 只信任明確設定的 frontend-platform 專用網段；主機模式未設定或 CIDR 無效時信任集合為空。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.RequireHeaderSymmetry = true;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    if (TrustedProxyNetwork.TryParse(cfg["TRUSTED_PROXY_CIDR"], out var network))
    {
        options.KnownIPNetworks.Add(network!.Value);
    }
});

// ---------------------------------------------------------------------------
// 全域例外處理
// ---------------------------------------------------------------------------
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ---------------------------------------------------------------------------
// LLM 端點的 per-IP 節流(修成本放大 / DoS):/api/chat、/api/chat/stream 為 AllowAnonymous,
// /api/copilot/agui 為 P1 後要求認證但仍會打真模型,三者皆未授權/授權後仍可迴圈燒額度、壓連線。
// 固定視窗每 IP 每分鐘 30 次,超限回 429。
// GlobalLimiter 對非這三條路徑一律 NoLimiter 放行(不必逐端點掛 policy,MapAGUI 這種 minimal API 端點也涵蓋)。
// Testing 環境不註冊/不套用；RateLimitingTesting 專供限流整合測試。
// ---------------------------------------------------------------------------
var rateLimitingEnabled = !builder.Environment.IsEnvironment("Testing");
if (rateLimitingEnabled)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, ct) => new ValueTask(ApiErrorWriter.WriteAsync(
            context.HttpContext.Response,
            StatusCodes.Status429TooManyRequests,
            "請求過於頻繁，請稍後再試",
            ct));
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var path = context.Request.Path;
            var limited = path == "/api/chat"
                || path == "/api/chat/stream"
                || path.StartsWithSegments("/api/copilot/agui");
            if (!limited)
            {
                return RateLimitPartition.GetNoLimiter("__unlimited");
            }

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        });
    });
}

// ---------------------------------------------------------------------------
// Observability:OpenTelemetry Tracing(業務 span source「chat.service」+ AspNetCore + OTLP)。
// 測試環境不掛 OTLP exporter,避免對 Langfuse 發送造成雜訊。
// ---------------------------------------------------------------------------
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("chat.service");
        // Agent Framework/Microsoft.Extensions.AI 的 GenAI span source(若底層有發出即一併收集)。
        tracing.AddSource("Experimental.Microsoft.Extensions.AI");
        tracing.AddAspNetCoreInstrumentation(o =>
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/actuator/health"));

        if (!builder.Environment.IsEnvironment("Testing")
            && !builder.Environment.IsEnvironment("RateLimitingTesting"))
        {
            tracing.AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(cfg["LANGFUSE_OTEL_ENDPOINT"]
                    ?? "http://localhost:3000/api/public/otel/v1/traces");
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                // OTLP headers 是「key=value」逗號分隔字串;值含空格("Basic xxx")直接放即可。
                o.Headers = "Authorization=" + (cfg["LANGFUSE_OTEL_AUTH"]
                    ?? "Basic cGstbGYtMTIzNDU2Nzg5MDpzay1sZi0xMjM0NTY3ODkw");
            });
        }
    });

var app = builder.Build();

// 使用者/租戶/聊天歷史/文件/組態皆改存 backend(postgres),platform 不再自帶 DB,啟動時無建表/種子。

app.UseExceptionHandler();

// 只讓 frontend-platform 專用 compose network 的 proxy forwarding headers 改寫 RemoteIpAddress；
// 限流才能安全採用真實 client IP，主機直連時偽造 X-Forwarded-For 仍會被忽略。
app.UseForwardedHeaders();

// 匿名 LLM 端點節流(Testing 預設不套用,見上方註冊)。
if (rateLimitingEnabled)
{
    app.UseRateLimiter();
}

// 刻意不用 UseHttpsRedirection:容器內對外是 http(:8080)。
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// 健康檢查:供容器探針;AllowAnonymous、免 token。OTel filter 過濾的正是這條路徑。
app.MapGet("/actuator/health", () => Results.Ok(new { status = "UP" })).AllowAnonymous();

// ---------------------------------------------------------------------------
// AG-UI 端點:CopilotKit 前端經此與「操作助理」對話(HTTP POST + SSE)。
// 與 /api/chat 的 AllowAnonymous 姿態刻意不同:副駕是操作應用程式/查租戶資料,
// 匿名副駕沒有有意義的行為,故要求認證——未帶/帶無效 JWT 一律 401。
// ---------------------------------------------------------------------------
app.MapAGUI(copilotAgent, "/api/copilot/agui").RequireAuthorization();

app.Run();

// 讓 WebApplicationFactory<Program> 測試能引用進入點。
public partial class Program;
