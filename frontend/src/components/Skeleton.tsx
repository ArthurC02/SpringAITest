/**
 * 齊一 loading 骨架：rows 條圓角灰塊 + 微光動畫（受 prefers-reduced-motion 保護，
 * 減動偏好下退為靜態灰塊）。純裝飾，對輔助技術隱藏。各視圖共用同一套 class。
 */
export default function Skeleton({ rows = 3 }: { rows?: number }) {
  return (
    <div className="skeleton-group" aria-hidden="true">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="skeleton skeleton-row" />
      ))}
    </div>
  )
}
