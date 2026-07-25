// 六個「fake」儲存庫已升格為生產碼(Backend.Api.Data.InMemory.*,DB_PROVIDER=inmemory 用)。
// 測試沿用原本的 Fake* 名稱以維持零改動:此處以別名 re-export 到升格後的實作。
// 一份事實 — 測試打的正是 Lite 模式跑的同一份記憶體儲存庫,不再有兩套會漂移的實作。
global using FakeAuthRepository = Backend.Api.Data.InMemory.InMemoryAuthRepository;
global using FakeConversationRepository = Backend.Api.Data.InMemory.InMemoryConversationRepository;
global using FakeRagRepository = Backend.Api.Data.InMemory.InMemoryRagRepository;
global using FakeConfigRepository = Backend.Api.Data.InMemory.InMemoryConfigRepository;
global using FakeSkillRepository = Backend.Api.Data.InMemory.InMemorySkillRepository;
global using FakeConfigurationSetRepository = Backend.Api.Data.InMemory.InMemoryConfigurationSetRepository;
global using FakeAgentRepository = Backend.Api.Data.InMemory.InMemoryAgentRepository;
global using FakeAgentRunRepository = Backend.Api.Data.InMemory.InMemoryAgentRunRepository;
