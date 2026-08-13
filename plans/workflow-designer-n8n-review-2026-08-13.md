# WorkflowDesigner n8n 互動改版 — 審查修正清單(2026-08-13)

來源:`code-reviewer` 對 D4 WorkflowDesigner 兩輪改版(本地投影模式 + n8n 完整互動)的審查,
每項均以真瀏覽器 probe 驗證過。未勾選項 = 剩餘工作,可直接續跑。

## 高

- [x] **畫布鍵盤陷阱:`Tab` 與 `Shift+Tab` 都被吃掉**
      `frontend/src/workflowDesigner/WorkflowDesigner.tsx:429-431` 的 Tab 攔截條件未排除 `event.shiftKey`,
      且 undo/redo/自動排版/`?`/React Flow `<Controls>`/適應畫面等按鈕全都位於 `.workflow-designer__canvas` 內,
      因此焦點一進畫布區就無法用 Tab/Shift+Tab 離開,那五顆按鈕變成純滑鼠可用(WCAG 2.1.2 No Keyboard Trap)。
      實測:焦點在「自動排版」按鈕時,Tab 與 Shift+Tab 都開啟 picker。
      這也推翻了 `WorkflowDesigner.tsx:560-561` 註解自陳的「Shift+Tab 退出」。
      修法:收斂為 `event.key === 'Tab' && !event.shiftKey && event.target === canvasRef.current`
      (只有焦點真的落在畫布容器/pane 本身才攔截),Shift+Tab 一律放行;
      並把 `.workflow-designer__toolbar` 移出畫布 div,或在 `event.target` 為 button/a/input 時不攔截。
      同時修正該處註解使其與實際行為一致。

## 中

- [x] **NodePicker 關閉後焦點掉到 `<body>`(`useOverlay` 抓錯觸發元素)**
      `frontend/src/workflowDesigner/overlay.ts:25-39`、`NodePicker.tsx:60-61`。
      `<input autoFocus>` 的 focus 在 React commit(layout)階段執行,早於 passive `useEffect`,
      因此 `useOverlay` 捕捉到的 `trigger` 是 picker 自己的搜尋框,卸載時已 `isConnected === false`,焦點不還原。
      實測:焦點在「＋ 新增節點」→ 開 picker → Escape → `document.activeElement` 為 BODY。
      違反規格第五節「關閉後焦點回到觸發元素」。ContextMenu 因聚焦走 passive effect(註冊在 useOverlay 之後)未受影響。
      修法:比照 `src/components/Modal.tsx:47`,在 render 階段(非 effect)以 ref 捕捉 trigger,
      或由呼叫端把觸發元素當 prop 傳入。

- [x] **全域快捷鍵吃掉整頁原生 `Ctrl+C` / `Ctrl+A` / `Escape`**
      `WorkflowDesigner.tsx:419-457`:監聽掛在 `window` capture,`take()` 一律 `preventDefault()` + `stopPropagation()`,
      焦點守衛只擋 input/textarea/select/contenteditable/dialog。
      實測(焦點在畫布外的「儲存草稿」按鈕):Ctrl+C / Ctrl+A / Escape 皆被 prevented;
      原生 `copy` 事件計數在進入編輯器後歸零 —— 使用者選取 inspector 的節點 id(`WorkflowDesigner.tsx:608`)、
      驗證錯誤訊息、版本 hash 後按 Ctrl+C **複製不到任何東西**,而 designer 的 `copySelection` 又因無選取節點回 null,靜默失敗。
      Escape 被 `stopPropagation` 攔截,非 designer 的 Escape 處理(含 CopilotKit 側欄)收不到。
      修法:監聽改掛 `canvasRef.current`,或 handler 開頭加範圍守衛
      `if (!canvasRef.current?.contains(event.target as Node)) return`(Ctrl+S 若需全頁生效單獨白名單);
      並在 `selectedNodeIds().length === 0` 時不要 `preventDefault` Ctrl+C,讓原生複製通過。

## 低

- [x] **再製/貼上可複製 `requiredStage` 節點,產生伺服器必定拒絕的圖**
      `WorkflowDesigner.tsx:266-275`(duplicate)、`clipboard.ts:45-81`(pasteClipboard 只過 `catalogForKind`)、
      右鍵選單 `WorkflowDesigner.tsx:385` 對 required 節點的「再製」仍 enabled。
      實測:選取 Required Gate → Ctrl+D → 儲存,definition 出現兩個 `required_gate`;
      `workflow/app/orchestration/validator.py:332` 會回 `duplicate_required_stage`,驗證/發布必定失敗。
      刪除方向 fail-closed 極嚴,新增方向卻不設防;`cut` 已排除不可刪節點,`duplicate`/`paste` 未對稱處理。
      修法:`pasteClipboard` 的 `allowed` 過濾旁加「該型別 `requiredStage === true` 且 definition 已存在同型別 → 丟棄」;
      右鍵選單與 NodeToolbar 的「再製」對 required 節點 disabled + `title` 說明。

- [x] **`Ctrl+A` + `Delete` 會刪光受保護 required 節點的所有連線**
      `WorkflowDesigner.tsx:205-208`:`onEdgesChange` 的邊刪除無任何保護判準。
      實測 n1 → n2(required) → n3,Ctrl+A + Delete 後留下孤立的必要階段(nodes 1 / edges 0)。
      `workflowDesignerN8n.ui.spec.ts:291-302` 的 fixture 沒有邊,此行為完全未被斷言。
      修法:決定語意後釘住 —— 若視為預期則補測試;若不預期,在 `commitRemoval` 中剔除
      「source/target 節點在同批 nodeIds 中卻未被刪掉」的邊。
      **決定(2026-08-13):視為預期,補測試釘住。** 理由:React Flow 的邊刪除比節點刪除早一個
      change 批次送達,前端無從分辨「節點連帶刪除」與「使用者明確刪這條線」(右鍵「刪除連線」是
      合法操作,不可一起擋);孤立的必要階段是可見結果、可 Ctrl+Z 復原,且 workflow validator 會以
      `unreachable_node`/`dead_end`/`governance_stage_order` 擋下驗證與發布,沒有靜默旁路。
      判準寫在 `WorkflowDesigner.tsx` 的 `commitRemoval` 註解,測試用「兩個相連的必要節點」fixture。

- [x] **undo 之後的方向鍵 nudge 吞掉 redo 分支且不留 undo 點**
      `frontend/src/workflowDesigner/history.ts:47` 的 coalesce 分支只看「距上次 nudge < 600ms」
      (`WorkflowDesigner.tsx:296-298`),不看上一筆歷史是否同一串 nudge。
      序列「nudge → Ctrl+Z(600ms 內)→ nudge」會 `past` 不變 + `future` 清空:redo 消失,
      這次移動也沒有 undo 點,再按 Ctrl+Z 會跳過兩步。
      `workflowDesignerEditing.unit.spec.ts:74-83` 目前把此行為當正確結果釘住,需一併修正。
      修法:`nudgeAt` 在 undo/redo/任何非 nudge commit 時歸零,或把 coalesce 條件從時間窗改為
      「上一筆 push 也是 nudge」的旗標。

- [x] **NodePicker 的 ARIA 組合不完整且無焦點圍堵**
      `frontend/src/workflowDesigner/NodePicker.tsx:57-65`:`aria-activedescendant`/`aria-controls` 掛在
      `type="search"` 的 input 上,但缺 `role="combobox"`/`aria-expanded`,螢幕閱讀器不保證朗讀,
      鍵盤上下移動無語音回饋;外層 `role="dialog"` 缺 `aria-modal`,且 picker 開啟時全域快捷鍵整段 early-return
      (`WorkflowDesigner.tsx:422`),Tab 會把焦點帶出仍開著的 picker。
      修法:input 加 `role="combobox" aria-expanded="true" aria-autocomplete="list"`;
      加 `aria-modal="true"` 並在 `onKeyDown` 內把 Tab 收斂回搜尋框。

- [x] **唯讀封口與 dirty 收尾的測試覆蓋缺口**
      `frontend/tests/workflowDesignerN8n.ui.spec.ts:339-369` 未斷言 `Ctrl+X`、`Ctrl+D`、`Shift+Alt+T`
      三個寫入快捷鍵,也未斷言 undo/redo 兩顆按鈕的 disabled 狀態(程式碼上守衛成立,但無測試釘住);
      另無任何測試覆蓋規格第二節收尾條件「undo 回到已儲存狀態後 `dirty` 回 false → 驗證/模擬按鈕重新可用」。
      修法:於既有唯讀測試補上述三個按鍵與兩顆歷史按鈕的 `toBeDisabled()`;新增一條 undo→dirty 行為測試。

## 審查確認無虞(不需處理,留檔備查)

fail-closed 九條寫入路徑無旁路;`maxConnections` 逐條累加判定正確;D4 契約(ui_metadata 四欄位、
RF 本地狀態未洩漏、UI-only 不改語意 hash)成立;偏離 (a) 單一位置提交路徑不漏不重複(含多選群組拖曳);
偏離 (b) commit ref 無 stale closure 且未破功 memo;undo/redo 的 JSON.stringify 前提成立、上限截斷語意正確、
load() 重設歷史、dirty 自然回 false;`isTextEntryTarget` 覆蓋完整;ContextMenu a11y 完整;
新 CSS 只用 tokens 且對比皆 ≥ WCAG AA(light 5.72:1 / dark 7.04:1 等);效能 memo 與模組層常數到位;
新測試非假綠(移掉焦點守衛會真的紅)。
