// 五支範本的薄 metadata（規格 §3.1）。刻意不含任何 flow YAML、node 名或版本 ——
// 骨架原文是 workflow 內建 template-* 的唯一事實來源，前端只在 compose 時依 basedOn 取用。
export interface SkillTemplate {
  id: 'retrieval' | 'compare' | 'stats' | 'infer' | 'inspire'
  basedOn: `template-${SkillTemplate['id']}` // 指向 workflow 內建骨架名（標準連字號）
  label: string
  slotKind: 'nl_logic' | 'script' // 決定 rule 欄注入 instruction 還是 Python body
  openFields: Array<keyof SkillForm> // 簡單模式可填白名單
  labels: Partial<Record<keyof SkillForm, string>>
  // 'select' 不在型別內：渲染端（SimpleSkillEditor）只判斷 number/text，加 select
  // 要連渲染分支一起加，否則會靜默退化成 text input。
  inputWidgets: Partial<Record<keyof SkillForm, 'text' | 'textarea' | 'number'>>
}

export type SkillForm = Partial<
  Record<'name' | 'description' | 'rule' | 'topK' | 'sortBy' | 'metric' | 'period', string>
>

export const TEMPLATES: SkillTemplate[] = [
  {
    id: 'retrieval',
    basedOn: 'template-retrieval',
    label: '知識問答',
    slotKind: 'nl_logic',
    // topK 移除：retrieval 走 kb_query deps，骨架無 # __SLOT_topK__ 槽（檢索筆數是 P4
    // config-set/deps 層的事，不是骨架純量槽）。列上去只會靜默丟棄使用者輸入。
    openFields: ['name', 'description', 'rule'],
    labels: { rule: '我的規則（用中文寫就好，可留空）' },
    inputWidgets: { rule: 'textarea' },
  },
  {
    id: 'compare',
    basedOn: 'template-compare',
    label: '比對排序',
    slotKind: 'script',
    openFields: ['name', 'description', 'rule', 'sortBy'],
    labels: { rule: '比較規則（Python）', sortBy: '排序依據' },
    inputWidgets: { rule: 'textarea', sortBy: 'text' },
  },
  {
    id: 'stats',
    basedOn: 'template-stats',
    label: '統計聚合',
    slotKind: 'script',
    openFields: ['name', 'description', 'rule', 'metric', 'period', 'topK'],
    labels: {
      rule: '統計規則（聚合，Python）',
      metric: '統計指標',
      period: '統計期間',
      topK: '檢索筆數（通常較高）',
    },
    inputWidgets: { rule: 'textarea', metric: 'text', period: 'text', topK: 'number' },
  },
  {
    id: 'infer',
    basedOn: 'template-infer',
    label: '推論',
    slotKind: 'nl_logic',
    openFields: ['name', 'description', 'rule'],
    labels: { rule: '推論規則' },
    inputWidgets: { rule: 'textarea' },
  },
  {
    id: 'inspire',
    basedOn: 'template-inspire',
    label: '啟發',
    slotKind: 'nl_logic',
    openFields: ['name', 'description', 'rule'],
    labels: { rule: '啟發角度' },
    inputWidgets: { rule: 'textarea' },
  },
]
