/** 把 ISO 時間字串轉成本地化顯示；解析失敗時原樣回傳。 */
export function fmtDate(s: string): string {
  const d = new Date(s)
  return Number.isNaN(d.getTime()) ? s : d.toLocaleString()
}
