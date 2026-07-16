import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import remarkMath from 'remark-math'
import rehypeHighlight from 'rehype-highlight'
import rehypeKatex from 'rehype-katex'
import 'highlight.js/styles/github-dark.css'
import 'katex/dist/katex.min.css'

/**
 * gpt-4o-mini 用 LaTeX 風格分隔符 \[ \] （區塊）與 \( \) （行內），
 * 但 remark-math 只認 $$ $$ / $ $，所以渲染前先轉換分隔符。
 * 預期轉換：
 *   normalizeMath('YoY = \\[ \\frac{a}{b} \\]') === 'YoY = $$ \\frac{a}{b} $$'
 *   normalizeMath('rate \\( x \\) high') === 'rate $ x $ high'
 * ponytail: 全域替換，假設聊天內容不會在 fenced code block 裡出現字面 \[ / \(
 * — 以此聊天情境可接受;真的踩雷再升級成 fence-aware 版本。
 */
export function normalizeMath(src: string): string {
  return src
    .replace(/\\\[([\s\S]+?)\\\]/g, '$$$$$1$$$$')
    .replace(/\\\(([\s\S]+?)\\\)/g, '$$$1$$')
}

/**
 * 把 AI 回覆當 Markdown 渲染：
 * - remark-gfm：表格、刪除線、任務清單、自動連結
 * - remark-math + rehype-katex：LaTeX 數學公式
 * - rehype-highlight：程式碼區塊語法高亮（highlight.js github-dark 主題）
 * react-markdown 預設不渲染原始 HTML，因此 LLM 內容不會造成 XSS。
 */
export default function Markdown({ children }: { children: string }) {
  return (
    <div className="markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath]}
        rehypePlugins={[rehypeHighlight, rehypeKatex]}
        components={{
          a({ node: _node, ...props }) {
            return <a {...props} target="_blank" rel="noreferrer noopener" />
          },
        }}
      >
        {normalizeMath(children)}
      </ReactMarkdown>
    </div>
  )
}
