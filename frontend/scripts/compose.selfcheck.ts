// compose 純函式自我檢查（規格 §13.3 / SSR-P2B-003..005、SSR-P4-013）。
// 不打網路、不呼叫 validateSkill。Node 25 原生 strip types：`node scripts/compose.selfcheck.ts`。
import assert from 'node:assert/strict'
import { compose } from '../src/skills/compose.ts'
import type { SkillTemplate } from '../src/skills/templates.ts'

const nlTemplate: SkillTemplate = {
  id: 'retrieval',
  basedOn: 'template_retrieval',
  label: '知識問答',
  slotKind: 'nl_logic',
  openFields: ['name', 'description', 'rule', 'topK'],
  labels: {},
  inputWidgets: {},
}

const scriptTemplate: SkillTemplate = {
  ...nlTemplate,
  id: 'compare',
  basedOn: 'template_compare',
  slotKind: 'script',
}

// 假骨架：block scalar 的 __RULE_SLOT__（8 空格縮排）+ 尾註 slot + 非白名單行。
const nlSkeleton = [
  'name: template_retrieval          # __SLOT_name__',
  'description: 骨架描述               # __SLOT_description__',
  'required_role: USER',
  'flow:',
  '  - node: retrieve@1.0',
  '    params:',
  '      top_k: 8                    # __SLOT_topK__',
  '  - node: nl_logic@1.0',
  '    params:',
  '      instruction: |',
  '        __RULE_SLOT__',
  '      output_key: business_result',
].join('\n')

// --- ① 規則逐行取代 + 縮排保留；② 多行含冒號/引號/中文/關鍵字不破 YAML ---
const multiRule = 'if x == "y": # 中文註解\n    return not True\n別的一行：含冒號'
const out1 = compose(nlTemplate, { name: 'my_skill', rule: multiRule }, nlSkeleton)
assert.ok(!out1.includes('__RULE_SLOT__'), '① sentinel 應消失')
assert.ok(
  out1.includes('        if x == "y": # 中文註解'),
  '① 第一行套 8 空格縮排',
)
assert.ok(out1.includes('            return not True'), '② 第二行保留相對縮排（8+4）')
assert.ok(out1.includes('        別的一行：含冒號'), '② 含冒號行字面保留')

// --- ③ 白名單覆寫（字串加引號、數字原樣）；④ 非白名單行原樣 ---
const out2 = compose(
  nlTemplate,
  { name: 'my_skill', description: '包含 " 引號', topK: '17' },
  nlSkeleton,
)
assert.ok(out2.includes('name: "my_skill"          # __SLOT_name__'), '③ name 被覆寫並加引號')
assert.ok(out2.includes('description: "包含 \\" 引號"'), '③ description 跳脫引號')
assert.ok(out2.includes('top_k: 17                    # __SLOT_topK__'), '③ topK 數字原樣（SSR-P4-013）')
assert.ok(out2.includes('required_role: USER'), '④ 非白名單行原樣')
// 未提供的欄位（sortBy/metric/period）不存在對應 slot 行 → 骨架原值不動，這裡沒有該行即可。

// --- SSR-P2B-005：rule 空 → 安全 no-op（NL 照原樣回答 / script pass），且無殘留 sentinel ---
const nlEmpty = compose(nlTemplate, {}, nlSkeleton)
assert.ok(!nlEmpty.includes('__RULE_SLOT__'), 'NL 空規則：sentinel 消失')
assert.ok(nlEmpty.includes('        照原樣回答'), 'NL 空規則：填安全指令')

const scriptSkeleton = ['flow:', '  - script: |', '      __RULE_SLOT__'].join('\n')
const scriptEmpty = compose(scriptTemplate, {}, scriptSkeleton)
assert.ok(!scriptEmpty.includes('__RULE_SLOT__'), 'script 空規則：sentinel 消失')
assert.ok(scriptEmpty.includes('      pass'), 'script 空規則：填 pass')

// --- Bug 1：script slot 的 Python 賦值形 slot（等號、無冒號）也被覆寫 ---
const pySlotSkeleton = [
  'flow:',
  '  - script: |',
  '      sort_by = "score"  # __SLOT_sortBy__',
  '      # __RULE_SLOT__',
].join('\n')
const pyOut = compose(scriptTemplate, { sortBy: 'metric' }, pySlotSkeleton)
assert.ok(pyOut.includes('sort_by = "metric"  # __SLOT_sortBy__'), 'Bug 1：Python 賦值形 slot 被覆寫')

// --- Bug 2：欄位有值但骨架無對應槽 → 靜默無作用、其餘不受影響、不 crash ---
const noSlot = compose(nlTemplate, { name: 'x', topK: '99' }, nlSkeleton.replace(/^.*__SLOT_topK__.*$/m, '      top_k: 8'))
assert.ok(noSlot.includes('top_k: 8'), 'Bug 2：無 topK 槽時該欄無作用，骨架原值不動')
assert.ok(noSlot.includes('name: "x"'), 'Bug 2：其餘欄位仍正常覆寫')
assert.ok(!noSlot.includes('99'), 'Bug 2：無槽欄位的值不外洩到輸出')

console.log('compose.selfcheck: all assertions passed')
