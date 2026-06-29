export interface Message {
  id: string
  role: 'user' | 'assistant'
  content: string
  /** true 代表這則 assistant 泡泡其實是錯誤訊息（以純文字、紅底顯示）。 */
  error?: boolean
}
