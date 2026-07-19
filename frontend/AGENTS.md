# Frontend SPA (React 19 + Vite + TypeScript, `:5173`)

Area-specific guidance. Cross-service contracts (SSE formats, ApiError shape, snake/camel field naming, 202 flow, auth) live in the repo-root [AGENTS.md](../AGENTS.md) — the API contract is defined by platform's actual responses, never invent fields.

## Layout

- `src/api/` — `http.ts` (`apiFetch`: Bearer injection, ApiError parsing, global 401 logout), `auth.ts`, `chat.ts` (SSE streaming), `documents.ts`, `skills.ts` (CRUD + validate + invoke), `nodes.ts` (catalog), `analysis.ts`, `config.ts`.
- `src/hooks/` — `useAuth`, `useChat`, `useDocuments`, `useResource` (shared read-only load state machine: data/loading/error/reload).
- `src/components/` — `AuthPage`, `AppShell` (sidebar + four-view switching + CopilotKit readables/actions), `ChatView`, `DocumentsView`, `AnalysisView`, `ConfigView` (three tabs: `skill` → `SkillHome`, `nodeParams` → `NodeParamsTab`, `general` → `GeneralConfigTab` internal function); Skill-related: `AdvancedSkillEditor`, `SimpleSkillEditor`, `SkillRunPanel`, `SkillHistory`, `NodeCatalog`, `YamlEditor` (syntax highlighting), `PythonEditor`, `TraceView` (invoke results).
- `src/types.ts` (shared types), `src/storageKeys.ts` (localStorage key constants), `src/format.ts` (utility: `fmtDate`). Dev proxy in `vite.config.ts`; container proxy in `nginx.conf` (SSE paths need `proxy_buffering off`).

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
- **Design tokens & shared UX primitives:** semantic colors are tokens in `index.css` (`--danger/--success/--warning` + `-bg` tints, `--on-accent`) with dark-mode overrides — never hardcode `#dc2626`-style values; every text/background pair must meet WCAG AA 4.5:1 in both modes. Reuse the shared primitives instead of per-view one-offs: `Toast.tsx` (`useToast`, success feedback), `Skeleton.tsx` (first-load only — never on refresh of existing data, and loading must not render as "no data"), `ErrorBoundary.tsx` (wraps the view area, keyed by view). Animations/transitions must stay inside the `prefers-reduced-motion` guard; interactive elements rely on the global `:focus-visible` rule.
- **Chat scroll model:** three-state (anchored → auto-follow, scrolled-up → stop, "跳至最新" pill to re-anchor; sending a message force-re-anchors). Streaming appends must scroll with `behavior: 'auto'` — smooth scrolling races the `onScroll` near-bottom check and permanently disengages auto-follow; smooth is only for the user-initiated jump.
- **Every API call goes through `apiFetch`** — never hand-roll fetch with auth headers. Login stores `{token, username, role, tenantCode}` in localStorage; any 401 while logged in clears the session **and** the `springai-chat:*` keys (cross-user privacy on shared browsers), then returns to login with a dismissible "session expired" notice. **Chat state persistence:** `useChat` auto-saves to localStorage with 500ms debounce + forces a flush on component unmount and `beforeunload` to avoid message loss on tab close. Logout must invalidate the active persistence generation before clearing storage so delayed debounce/unmount flushes cannot restore the previous user's messages.
- **Chat SSE:** parse `data:` (no space) and `event:` (no space) via `fetch` + `ReadableStream` (`EventSource` cannot POST a body) — do not touch this contract. Normal data frames contain tokens; `event:error` signals mid-stream failure (platform already sent partial response, now sending this terminal frame with a fixed Chinese error message). `userId` = logged-in username (mem0 grouping); `conversationId` = stable UUID per conversation, regenerated on "new conversation". Mid-stream errors are caught in `streamChat()`, the error message is thrown (caught by `useChat`), and the chat shows the already-rendered partial content + an error bubble.
- **Documents 202 flow:** optimistically insert a `processing` row, poll `GET /api/documents` every 2s (60s cap). The poll merge uses `useDocuments`' `pendingRef` pattern — a full list replacement must never wipe the optimistic row before the server catches up (eventual consistency).
- **CopilotKit sidebar:** `@copilotkit/react-*` pinned EXACT (currently 1.62.3); `@ag-ui/client` pinned to the exact version react-core depends on internally (currently 0.0.57) — a mismatch causes `HttpAgent is not assignable to AbstractAgent` type errors. `App.tsx` connects the browser directly to platform via `new HttpAgent({url: '/api/copilot/agui'})` + the `agents__unsafe_dev_only` prop (dev-only API, accepted for this POC; productionizing means an official CopilotRuntime Node bridge instead).
- **CopilotKit safety rules:** NEVER put `session.token` in a `useCopilotReadable`. Actions run client-side through `apiFetch` so platform authorization stays the real boundary. `renderAndWaitForResponse` confirm buttons are bare React handlers — always try/catch and call `respond()` on both success and failure, or the LLM run hangs.
- ADMIN-only Config view is hidden by sidebar filtering; backend role enforcement is the real boundary (the UI check is UX, not security).
