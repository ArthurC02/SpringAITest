// Configuration Set 範圍檢查純函式自我檢查（設計 §9 / SSR-P4-019）。
// 不打網路。Node 25 原生 strip types：`node scripts/nodeParams.selfcheck.ts`。
import assert from 'node:assert/strict'
import { draftToValues, validateConfigValues } from '../src/nodeParams.ts'

// --- ① 合法值全通過 ---
assert.deepEqual(
  validateConfigValues({
    'retrieval.top_k': 50,
    'kb_query.top_k': 8,
    'kb_query.max_retrieval_attempts': 2,
    'workflow.timeout_seconds': 120,
    'llm.model': 'gpt-4o-mini',
    'intent.confidence_threshold': 0.6,
    'llm.temperature': 0.7,
  }),
  {},
  '① 合法值無錯誤',
)

// --- ② 上界越界（retrieval.top_k 1–50） ---
assert.ok(validateConfigValues({ 'retrieval.top_k': 99 })['retrieval.top_k'], '② >50 被擋')
assert.ok(validateConfigValues({ 'retrieval.top_k': 0 })['retrieval.top_k'], '② <1 被擋')

// --- ③ 整數鍵拒小數 ---
assert.ok(validateConfigValues({ 'kb_query.top_k': 1.5 })['kb_query.top_k'], '③ int 鍵拒小數')

// --- ④ float 範圍（temperature 0–2、threshold 0–1） ---
assert.ok(validateConfigValues({ 'llm.temperature': 2.5 })['llm.temperature'], '④ temp >2 被擋')
assert.equal(validateConfigValues({ 'llm.temperature': 2 })['llm.temperature'], undefined, '④ temp=2 邊界通過')
assert.ok(validateConfigValues({ 'intent.confidence_threshold': 1.1 })['intent.confidence_threshold'], '④ 門檻 >1 被擋')

// --- ⑤ 白名單外的模型被擋 ---
assert.ok(validateConfigValues({ 'llm.model': 'gpt-5-ultra' })['llm.model'], '⑤ 非白名單模型被擋')
assert.equal(validateConfigValues({ 'llm.model': 'mock-gpt' })['llm.model'], undefined, '⑤ 白名單模型通過')

// --- ⑥ 未填/空字串的鍵不檢查、不送出 ---
assert.deepEqual(validateConfigValues({ 'retrieval.top_k': '' }), {}, '⑥ 空字串跳過檢查')
assert.deepEqual(
  draftToValues({ 'retrieval.top_k': '10', 'kb_query.top_k': '', 'llm.model': 'gpt-4o-mini' }),
  { 'retrieval.top_k': 10, 'llm.model': 'gpt-4o-mini' },
  '⑥ 只收有值鍵，數值轉 number、select 留字串',
)

console.log('nodeParams.selfcheck: all assertions passed')
