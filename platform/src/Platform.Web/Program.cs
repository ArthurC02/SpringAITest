using System.ClientModel;
using Platform.Service;
using Platform.Service.Abstractions;
using Platform.Service.Options;
using Platform.Web.Auth;
using Platform.Web.Errors;
using Platform.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Agents.AI;
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
builder.Services.AddSingleton<IChatMemoryStore, InMemoryChatMemoryStore>();

// LLM 代理:Agent Framework 實作,無狀態、單例即可。
builder.Services.AddSingleton<ILlmAgent>(_ => new AgentFrameworkLlmAgent(llmOptions));

// ---------------------------------------------------------------------------
// AG-UI(CopilotKit 操作助理):同一套 LiteLLM 佈線。IChatClient 註冊為單例,
// 讓 AG-UI 端點的 AIAgent 由它建立;測試可替換為 fake IChatClient 免打真 LLM。
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IChatClient>(_ =>
    new OpenAIClient(
        new ApiKeyCredential(llmOptions.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(llmOptions.BaseUrl) })
    .GetChatClient(llmOptions.ChatModel)
    .AsIChatClient());
builder.Services.AddAGUI();

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

// ---------------------------------------------------------------------------
// 全域例外處理
// ---------------------------------------------------------------------------
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

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

        if (!builder.Environment.IsEnvironment("Testing"))
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

// 刻意不用 UseHttpsRedirection:容器內對外是 http(:8080)。
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// ---------------------------------------------------------------------------
// AG-UI 端點:CopilotKit 前端經此與「操作助理」對話(HTTP POST + SSE)。
// 系統提示定位為操作本平台的助理;實際操作工具由前端以 AG-UI client tools 提供,
// 助理只需正常回答並在需要時呼叫收到的工具。
// ---------------------------------------------------------------------------
const string copilotInstructions =
    "你是本系統的操作助理,協助使用者操作這個 AI 資料檢索與分析平台:" +
    "查詢與管理文件、執行工作流(例如檢索式問答)、查看分析摘要、切換視圖。" +
    "請一律以繁體中文回答,簡潔專業。當使用者的請求需要實際操作時," +
    "呼叫前端提供的工具(client tools)來完成;你只需正常回答並在需要時呼叫收到的工具。";

var copilotAgent = app.Services.GetRequiredService<IChatClient>().AsAIAgent(
    instructions: copilotInstructions,
    name: "OperationsAssistant");

// ponytail: AllowAnonymous 開發姿態,與 /api/chat 一致(無 RequireAuthorization)。
// 升級路徑:改 .RequireAuthorization() 並讓瀏覽器端 HttpAgent/runtime 轉發 Authorization header。
app.MapAGUI("/api/copilot/agui", copilotAgent).AllowAnonymous();

app.Run();

// 讓 WebApplicationFactory<Program> 測試能引用進入點。
public partial class Program;
