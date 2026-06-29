import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeHighlight from 'rehype-highlight'
import 'highlight.js/styles/github-dark.css'

/**
 * 把 AI 回覆當 Markdown 渲染：
 * - remark-gfm：表格、刪除線、任務清單、自動連結
 * - rehype-highlight：程式碼區塊語法高亮（highlight.js github-dark 主題）
 * react-markdown 預設不渲染原始 HTML，因此 LLM 內容不會造成 XSS。
 */
export default function Markdown({ children }: { children: string }) {
  return (
    <div className="markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeHighlight]}
        components={{
          a({ node, ...props }) {
            return <a {...props} target="_blank" rel="noreferrer noopener" />
          },
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  )
}
