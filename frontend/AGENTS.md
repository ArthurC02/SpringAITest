# Frontend SPA (React 19 + Vite + TypeScript, `:5173`)

Area-specific guidance. Cross-service contracts (SSE formats, ApiError shape, snake/camel field naming, 202 flow, auth) live in the repo-root [AGENTS.md](../AGENTS.md) — the API contract is defined by platform's actual responses, never invent fields.

## Layout

- `src/api/` — `http.ts` (`apiFetch`: Bearer injection, ApiError parsing, global 401 logout), `auth.ts`, `chat.ts` (SSE streaming), `documents.ts`, `workflows.ts`, `analysis.ts`, `config.ts`.
- `src/hooks/` — `useAuth`, `useChat`, `useDocuments`.
- `src/components/` — `AuthPage`, `AppShell` (sidebar + view switching + CopilotKit readables/actions), `ChatView`, `DocumentsView`, `WorkflowsView`, `AnalysisView`, `ConfigView`.
- Shared types in `src/types.ts`. Dev proxy in `vite.config.ts`; container proxy in `nginx.conf` (SSE paths need `proxy_buffering off`).

## Commands (run from `frontend/`)

```bash
npm install
npm run dev     # Vite on :5173, proxies /api → :8080
npm run build   # tsc type-check + vite bundle
npm run lint    # oxlint
```

No dedicated test runner — lint + build are the gates; run both before any PR touching frontend.

## Conventions & Gotchas

- Component files `PascalCase.tsx`, hooks `useX.ts`, React function components only. No react-router — view switching is `useState` in `AppShell`. No UI library — hand-written CSS on the custom properties in `index.css`. Do not add dependencies without explicit authorization.
- **Every API call goes through `apiFetch`** — never hand-roll fetch with auth headers. Login stores `{token, username, role, tenantCode}` in localStorage; any 401 while logged in clears the session **and** the `springai-chat:*` keys (cross-user privacy on shared browsers), then returns to login with a dismissible "session expired" notice.
- **Chat SSE:** parse `data:` (no space) via `fetch` + `ReadableStream` (`EventSource` cannot POST a body) — do not touch this contract. `userId` = logged-in username (mem0 grouping); `conversationId` = stable UUID per conversation, regenerated on "new conversation".
- **Documents 202 flow:** optimistically insert a `processing` row, poll `GET /api/documents` every 2s (60s cap). The poll merge uses `useDocuments`' `pendingRef` pattern — a full list replacement must never wipe the optimistic row before the server catches up (eventual consistency).
- **CopilotKit sidebar:** `@copilotkit/react-*` pinned EXACT (currently 1.62.3); `@ag-ui/client` pinned to the exact version react-core depends on internally (currently 0.0.57) — a mismatch causes `HttpAgent is not assignable to AbstractAgent` type errors. `App.tsx` connects the browser directly to platform via `new HttpAgent({url: '/api/copilot/agui'})` + the `agents__unsafe_dev_only` prop (dev-only API, accepted for this POC; productionizing means an official CopilotRuntime Node bridge instead).
- **CopilotKit safety rules:** NEVER put `session.token` in a `useCopilotReadable`. Actions run client-side through `apiFetch` so platform authorization stays the real boundary. `renderAndWaitForResponse` confirm buttons are bare React handlers — always try/catch and call `respond()` on both success and failure, or the LLM run hangs.
- ADMIN-only Config view is hidden by sidebar filtering; backend role enforcement is the real boundary (the UI check is UX, not security).
