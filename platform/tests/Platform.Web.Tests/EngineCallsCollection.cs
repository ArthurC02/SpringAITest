namespace Platform.Web.Tests;

/// <summary>
/// 讓 ChatApiTests、CopilotAguiApiTests 與 SkillApiTests 序列化(同一 collection 不並行)。
/// 聊天 → Skill 路由後,已登入聊天每輪會呼叫 GetSkillCatalogAsync,累加靜態的
/// FakeWorkflowService.EngineCalls;而 SkillApiTests 以「請求前後 EngineCalls 不變」
/// 斷言 401 未觸及下游。兩類若並行,聊天的 catalog 呼叫會污染該計數 → 序列化消除競態。
/// (CatalogOverride/SkillInvokes 這些靜態注入點同理需要序列化;P4 起 CopilotAguiApiTests 也會
/// 改動它們,見該類別 XML doc。)
/// </summary>
[CollectionDefinition("EngineCalls")]
public sealed class EngineCallsCollection
{
}
