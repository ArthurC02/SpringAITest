using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Service.Tests;

/// <summary>
/// P2 審查建議的直接單元測試(copilot-shared-core P3 §11 步驟 11.5):「登入者 cid 必含
/// {tenant}:{user} 前綴」不能只靠 ChatService 端到端測試間接證明,補一條直接對純函式的斷言。
/// 同時是 <see cref="ChatMemoryKeyDerivation"/>(消除 ChatService.cs 與 HttpChatIdentityAccessor.cs
/// 曾各自維護一份相同邏輯的重複後)唯一的直接單元測試。
/// </summary>
public sealed class ChatMemoryKeyDerivationTests
{
    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");

    [Fact]
    public void LoggedIn_ConversationIdBlank_CidFallsBackToUid_ContainsTenantUserPrefix()
    {
        var (uid, cid) = ChatMemoryKeyDerivation.Derive(userId: "ignored-body-value", conversationId: "", UserA);

        Assert.Equal("demo-a:user-a", uid);
        Assert.Equal("demo-a:user-a", cid);
        Assert.StartsWith("demo-a:user-a", cid);
    }

    [Fact]
    public void LoggedIn_ConversationIdNonBlank_CidMustContainTenantUserPrefix()
    {
        var (uid, cid) = ChatMemoryKeyDerivation.Derive(userId: "ignored-body-value", conversationId: "c1", UserA);

        Assert.Equal("demo-a:user-a", uid);
        Assert.Equal("demo-a:user-a:c1", cid);
        Assert.StartsWith("demo-a:user-a:", cid);
    }

    [Fact]
    public void LoggedIn_BodyUserId_IsIgnored_IDORProtection()
    {
        // 攻擊者刻意送 body userId=別人的名字,已登入時必須完全被忽略,uid 只來自 JWT 身分。
        var (uid, _) = ChatMemoryKeyDerivation.Derive(userId: "victim", conversationId: null, UserA);

        Assert.Equal("demo-a:user-a", uid);
    }

    [Fact]
    public void Anonymous_BothBlank_FallsBackToDefault()
    {
        var (uid, cid) = ChatMemoryKeyDerivation.Derive(userId: null, conversationId: null, userCtx: null);

        Assert.Equal("default", uid);
        Assert.Equal("default", cid);
    }

    [Fact]
    public void Anonymous_UserIdProvided_ConversationBlank_CidFallsBackToUid()
    {
        var (uid, cid) = ChatMemoryKeyDerivation.Derive(userId: "u1", conversationId: "", userCtx: null);

        Assert.Equal("u1", uid);
        Assert.Equal("u1", cid);
    }

    // 匿名分支的最後一格組合(userId 空白 × conversationId 非空白):uid 退回 "default",但 cid 原封不動,
    // **不會**被前綴成 "default:conv1" —— 與已登入分支「cid 一律前綴 {tenant}:{user}」刻意不對稱。
    // 匿名沒有身分可隔離,所以兩個匿名呼叫端只要送同一個 conversationId 就共用同一個短期視窗;
    // 這是既有契約(匿名只有短期連續性,不 recall/remember/persist),在此釘住以免哪天被「順手補前綴」。
    [Fact]
    public void Anonymous_UserIdBlank_ConversationProvided_CidIsNotPrefixedWithUid()
    {
        var (uid, cid) = ChatMemoryKeyDerivation.Derive(userId: null, conversationId: "conv1", userCtx: null);

        Assert.Equal("default", uid);
        Assert.Equal("conv1", cid);
        Assert.DoesNotContain("default", cid);
    }

    // 前後空白「不」被 trim(ChatMemoryKeyDerivation.cs:25 直接字串插值):" c1 " 與 "c1" 是兩個不同的
    // 短期記憶視窗。這是刻意釘住的現況,因為 AgentChatRuntime.cs:41-42 對同一個 conversationId 會 Trim()
    // 後才送去 backend —— 兩邊的正規化不對稱,改動任一側都必須同時檢視另一側(D6 的 durable
    // conversation_id 與 session 命名空間會對不齊)。
    [Fact]
    public void LoggedIn_ConversationIdSurroundingWhitespace_IsNotTrimmed()
    {
        var (_, cid) = ChatMemoryKeyDerivation.Derive(userId: null, conversationId: "  c1  ", UserA);

        Assert.Equal("demo-a:user-a:  c1  ", cid);
    }

    // ---- 身分含 ':' 的撞鍵:fail closed ----
    // 鍵以 ':' 串接且不逃逸,tenant "t" + user "a:b" 與 tenant "t:a" + user "b" 會產生同一把 uid "t:a:b";
    // 短期視窗與 mem0 uid 都用它 → 撞鍵即跨使用者記憶可見。鍵格式刻意不改(既有 mem0 uid 與進行中的
    // 對話視窗會全斷),改成衍生時直接拒絕。backend 的註冊/登入邊界是第一道牆,這裡是第二道。

    [Theory]
    [InlineData("t", "a:b")]
    [InlineData("t:a", "b")]
    [InlineData("t:a", "b:c")]
    public void LoggedIn_IdentityContainingSeparator_FailsClosed(string tenantCode, string userId)
    {
        var userCtx = new UserContext(userId, tenantCode, "USER");

        Assert.Throws<InvalidOperationException>(
            () => ChatMemoryKeyDerivation.Derive(userId: null, conversationId: "c1", userCtx));
    }

    // 守門擋的是**身分欄位**,不是組合後的鍵:已登入的 cid 本來就被前綴成 "{tenant}:{user}:{cid}",
    // 且呼叫端可能把上一輪的完整 cid 再送回來 —— 這些合法的含 ':' 值必須照常衍生,不得被誤擋。
    [Fact]
    public void LoggedIn_ConversationIdContainingSeparator_IsNotBlocked()
    {
        var (uid, cid) = ChatMemoryKeyDerivation.Derive(
            userId: null, conversationId: "demo-a:user-a:c1", UserA);

        Assert.Equal("demo-a:user-a", uid);
        Assert.Equal("demo-a:user-a:demo-a:user-a:c1", cid);
    }

    // 匿名沒有 JWT 身分,完全不進隔離鍵衍生:body 的 userId/conversationId 含 ':' 仍保有短期連續性
    // (根 AGENTS.md 契約:匿名有短期連續性,但不 recall/remember/persist)。
    [Fact]
    public void Anonymous_ValuesContainingSeparator_KeepShortTermContinuity()
    {
        var (uid, cid) = ChatMemoryKeyDerivation.Derive(
            userId: "a:b", conversationId: "t:a", userCtx: null);

        Assert.Equal("a:b", uid);
        Assert.Equal("t:a", cid);
    }
}
