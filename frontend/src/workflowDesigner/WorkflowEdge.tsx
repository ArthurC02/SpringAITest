import { memo, useState, type MouseEvent } from 'react'
import { BaseEdge, EdgeLabelRenderer, getBezierPath, type EdgeProps } from '@xyflow/react'
import { useDesignerActions } from './DesignerActionsContext'

/** 工具列浮在線的上方，讓線的中點本身仍然可點選（點線＝選取邊，不會誤觸按鈕）。 */
const TOOLBAR_OFFSET = 24

function WorkflowEdgeLine({
  id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, markerEnd, label, selected,
}: EdgeProps) {
  const actions = useDesignerActions()
  const [hovered, setHovered] = useState(false)
  const [path, labelX, labelY] = getBezierPath({
    sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition,
  })
  const active = hovered || !!selected

  return (
    <>
      {/* <g> 讓 BaseEdge 的加寬互動區也能觸發 hover（handler 掛在細線上會很難 hover 到）。 */}
      <g onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)}>
        <BaseEdge
          id={id} path={path} markerEnd={markerEnd} interactionWidth={24}
          className={active ? 'workflow-edge--active' : undefined}
        />
      </g>
      {active && (
        <EdgeLabelRenderer>
          <div
            className="workflow-edge__tools"
            style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY - TOOLBAR_OFFSET}px)` }}
            onMouseEnter={() => setHovered(true)}
            onMouseLeave={() => setHovered(false)}
          >
            {/* 埠名平時隱藏，只在 hover/選取時出現，畫面才乾淨。 */}
            {label && <span className="workflow-edge__label">{label}</span>}
            {!actions.disabled && (
              <>
                <button
                  type="button" className="btn workflow-edge__btn" title="在此中插節點" aria-label="在此中插節點"
                  onClick={(event: MouseEvent) => actions.insertNodeOnEdge(id, { x: event.clientX, y: event.clientY })}
                >＋</button>
                <button
                  type="button" className="btn workflow-edge__btn" title="刪除連線" aria-label="刪除連線"
                  onClick={() => actions.deleteEdge(id)}
                >✕</button>
              </>
            )}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  )
}

export default memo(WorkflowEdgeLine)
