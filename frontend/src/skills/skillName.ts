// Skill 的 name 是技術識別碼(engine `name:`),依 Agent Skills 標準 ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$
// (小寫英數+連字號、不可首尾/連續連字號、可數字開頭、1–64 字)。
// 前端在存檔前先擋掉不合規輸入(例如中文),讓非技術使用者不會看到被誤標成 flow 的 server 錯誤;
// server 仍是真正的關卡,這裡只是 UX。

const NAME_PATTERN = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/

export const NAME_RULE_MESSAGE =
  '名稱只能用小寫英文、數字、連字號（-），不可首尾或連續連字號（1–64 字）；中文請放在說明'

export const isValidSkillName = (s: string) =>
  s.length <= 64 && !s.includes('--') && NAME_PATTERN.test(s)

/**
 * 從使用者輸入猜一個合法 slug:轉小寫、非 [a-z0-9] 連續段 → 單一 `-`、去頭尾 `-`、
 * 截到 64 字(截後再去尾 `-`)。數字開頭現在合法,故不再補前綴。
 * 全非拉丁(例如「測試」)無從音譯,退回通用預設 'skill'(不裝音譯庫 = 不加相依)。
 * 呼叫端把使用者原本打的中文留在「說明」,不丟棄。
 */
export function slugifySkillName(input: string): string {
  const s = input
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 64)
    .replace(/-+$/, '')
  return s === '' ? 'skill' : s
}
