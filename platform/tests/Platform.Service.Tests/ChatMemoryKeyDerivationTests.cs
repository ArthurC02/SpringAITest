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
}
