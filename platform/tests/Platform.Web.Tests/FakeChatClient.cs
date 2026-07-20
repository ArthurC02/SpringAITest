using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Platform.Web.Tests;

/// <summary>
/// AG-UI 端點的 IChatClient fake:串流吐「你好」「世界」兩塊,阻塞回固定字串。
/// 讓 AG-UI 冒煙測試走真的 MapAGUI/SSE 序列化,但不打真的 LiteLLM。
/// (單獨一檔避免與 Platform.Service.Dtos.ChatResponse 名稱衝突。)
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    /// <summary>
    /// 每次串流呼叫收到的 messages(依呼叫順序累積,含框架的 session 歷史注入)。
    /// 供租戶隔離等測試斷言「送進模型的內容」——不可只驗 nullity。單例(DI 註冊為 Singleton),
    /// 測試須自行挑選不與其他案例衝突的 threadId 以避免歷史互相污染。
    /// </summary>
    public List<IReadOnlyList<ChatMessage>> Runs { get; } = new();

    /// <summary>
    /// 補 G2(copilot-shared-core 02-spec §7.2):原本 GetService 恆回 null,搭配「無腳本化 function call」
    /// 使 AG-UI 的 client tools 迴路在單元層結構上不可能產生 tool call。這裡用最小腳本觸發:當最後一則
    /// user 訊息等於本常數、且本輪帶有非空 <see cref="ChatOptions.Tools"/> 時,直接吐出一個
    /// <see cref="FunctionCallContent"/>(取第一個工具的名稱,參數固定為 view=documents)。
    /// AG-UI client tools 只是「宣告」(無 server 端可執行委派),FunctionInvokingChatClient 無法本地執行,
    /// 因此會原樣把這個呼叫內容交給 AGUI 編碼層,冒泡成 TOOL_CALL_START/ARGS/END 事件(T-P4-1/B-P4-14)。
    /// </summary>
    public const string ToolCallTrigger = "請切換視圖";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "測試回覆")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Runs.Add(list);
        await Task.Yield();

        // P2:鏈路 A 的「未命中」純聊天也改跑共用 hosted agent(此 fake 因而同時扮演 AG-UI 與鏈路 A
        // 兩邊的底層 IChatClient),故沿用 Web.Tests 舊 FakeLlmAgent 依訊息內容腳本化的兩個特例,
        // 讓 A-17/A-22 等既有的鏈路 A 契約測試在新接縫下仍能驅動同樣的行為。
        var last = list.Count > 0 ? list[^1].Text ?? string.Empty : string.Empty;

        if (last == "多行")
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "甲\n乙");
            yield break;
        }

        if (last == "串流爆炸")
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "半截");
            throw new InvalidOperationException("串流中途失敗");
        }

        if (last == ToolCallTrigger && options?.Tools is { Count: > 0 } tools)
        {
            var toolName = tools[0].Name;
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-1", toolName, new Dictionary<string, object?> { ["view"] = "documents" }),
            });
            yield break;
        }

        // P2(B-P2-04 多輪去重測試):同一輪的所有 update 共用一個 messageId,跨輪不同——比照真實
        // OpenAI streaming completion id 的行為(單次回覆全程一致)。AGUI wire 層與 session 儲存都會
        // 沿用這個 id(ChatResponseExtensions.ProcessUpdate 反編譯證實),測試才能驗證 assistant 訊息
        // 的 id 對齊/不對齊兩種情境。
        var messageId = Guid.NewGuid().ToString("N");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "你好") { MessageId = messageId };
        yield return new ChatResponseUpdate(ChatRole.Assistant, "世界") { MessageId = messageId };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
