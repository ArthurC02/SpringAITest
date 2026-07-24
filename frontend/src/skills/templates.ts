// 五支範本的薄 metadata（規格 §3.1）。刻意不含任何 flow YAML、node 名或版本 ——
// 骨架原文是 workflow 內建 template-* 的唯一事實來源，前端只在 compose 時依 basedOn 取用。
// 五支範本的規則槽現在全是 nl_logic（instruction），故不再帶 slotKind 欄位。
export interface SkillTemplate {
  id: 'retrieval' | 'compare' | 'stats' | 'infer' | 'inspire'
  basedOn: `template-${SkillTemplate['id']}` // 指向 workflow 內建骨架名（標準連字號）
  label: string
  openFields: Array<keyof SkillForm> // 簡單模式可填白名單
  labels: Partial<Record<keyof SkillForm, string>>
  // 'select' 不在型別內：渲染端（SimpleSkillEditor）只判斷 number/text，加 select
  // 要連渲染分支一起加，否則會靜默退化成 text input。
  inputWidgets: Partial<Record<keyof SkillForm, 'text' | 'textarea' | 'number'>>
}

export type SkillForm = Partial<Record<'name' | 'description' | 'rule' | 'topK', string>>

export const TEMPLATES: SkillTemplate[] = [
  {
    id: 'retrieval',
    basedOn: 'template-retrieval',
    label: '知識問答',
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
    openFields: ['name', 'description', 'rule'],
    labels: { rule: '比較規則（用中文寫，例：依金額由大到小排序）' },
    inputWidgets: { rule: 'textarea' },
  },
  {
    id: 'stats',
    basedOn: 'template-stats',
    label: '統計聚合',
    openFields: ['name', 'description', 'rule', 'topK'],
    labels: {
      rule: '統計規則（用中文寫，例：加總每季營收）',
      topK: '檢索筆數（通常較高）',
    },
    inputWidgets: { rule: 'textarea', topK: 'number' },
  },
  {
    id: 'infer',
    basedOn: 'template-infer',
    label: '推論',
    openFields: ['name', 'description', 'rule'],
    labels: { rule: '推論規則' },
    inputWidgets: { rule: 'textarea' },
  },
  {
    id: 'inspire',
    basedOn: 'template-inspire',
    label: '啟發',
    openFields: ['name', 'description', 'rule'],
    labels: { rule: '啟發角度' },
    inputWidgets: { rule: 'textarea' },
  },
]
