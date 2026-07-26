import { useCallback, useEffect, useRef, useState } from 'react'
import {
  cancelAgentRun,
  getAgentRun,
  getAgentRunEvents,
  newIdempotencyKey,
  resumeAgentRun,
  startAgentTestRun,
} from '../api/agentRuns'
import type { AgentRun, AgentRunEvent } from '../types'
import { fmtDate } from '../format'
import {
  ACTIVE_RUN_STATUSES,
  isAmbiguousFailure,
  mergeRunEvents,
  POLL_MS,
  sanitizeRunDisplay,
  TERMINAL_RUN_STATUSES,
  withAcceptedCancelStatus,
} from '../agentRunDisplay'
import {
  AGENT_RUN_ATTEMPT_STORAGE_PREFIX,
  clearPendingCancelRun,
  getSessionStorage,
  LogicalAttemptKey,
  readPendingCancelRun,
  writePendingCancelRun,
} from '../logicalAttemptKey'
import ErrorText from './ErrorText'
import { useConfirm } from './ConfirmDialog'
import { useToast } from './Toast'

type Operation = 'starting' | 'resuming' | 'cancelling' | null
type AttemptSlot = { namespace: string; attempt: LogicalAttemptKey }

function statusLabel(status: string): string {
  const labels: Record<string, string> = {
    queued: '排隊中',
    pending: '等待啟動',
    starting: '啟動中',
    running: '執行中',
    resuming: '恢復中',
    cancelling: '取消中',
    waiting_input: '等待補充資訊',
    waiting_approval: '等待核准（D3 不提供核准操作）',
    completed: '已完成',
    failed: '失敗',
    cancelled: '已取消',
    timed_out: '已逾時',
  }
  return labels[status] ?? status
}

function JsonValue({ value }: { value: unknown }) {
  return <pre>{JSON.stringify(sanitizeRunDisplay(value), null, 2)}</pre>
}

interface Props {
  agentId: string
  publishedRevision: number
  enabled: boolean
}

export default function AgentTestConsole({ agentId, publishedRevision, enabled }: Props) {
  const toast = useToast()
  const confirm = useConfirm()
  const [message, setMessage] = useState('')
  const [resumeMessage, setResumeMessage] = useState('')
  const [run, setRun] = useState<AgentRun | null>(null)
  const [events, setEvents] = useState<AgentRunEvent[]>([])
  const [operation, setOperation] = useState<Operation>(null)
  const [requestError, setRequestError] = useState<string | null>(null)
  const [startFailed, setStartFailed] = useState(false)
  const [resumeFailed, setResumeFailed] = useState(false)
  const [startAmbiguous, setStartAmbiguous] = useState(false)
  const [resumeAmbiguous, setResumeAmbiguous] = useState(false)
  const latestSequenceRef = useRef(0)
  const currentRunIdRef = useRef<string | null>(null)
  const generationRef = useRef(0)
  const storageRef = useRef(getSessionStorage())
  const startAttemptRef = useRef<AttemptSlot | null>(null)
  const resumeAttemptRef = useRef<AttemptSlot | null>(null)
  const cancelAttemptRef = useRef<AttemptSlot | null>(null)

  const attemptFor = useCallback(function attemptFor(
    ref: { current: AttemptSlot | null },
    namespace: string,
  ): LogicalAttemptKey {
    if (ref.current?.namespace !== namespace) {
      ref.current = {
        namespace,
        attempt: new LogicalAttemptKey(newIdempotencyKey, storageRef.current, namespace),
      }
    }
    return ref.current.attempt
  }, [])

  const cancelAttemptFor = useCallback(function cancelAttemptFor(runId: string): LogicalAttemptKey {
    return attemptFor(
      cancelAttemptRef,
      `${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:cancel:${encodeURIComponent(runId)}`,
    )
  }, [attemptFor])

  const settleCancelledRun = useCallback(function settleCancelledRun(runId: string): void {
    cancelAttemptFor(runId).consumeIdentity([runId])
    clearPendingCancelRun(agentId, storageRef.current)
  }, [agentId, cancelAttemptFor])

  function isCurrentRequest(generation: number, runId: string): boolean {
    return generationRef.current === generation && currentRunIdRef.current === runId
  }

  function applyEventPage(
    page: Awaited<ReturnType<typeof getAgentRunEvents>>,
    generation: number,
    runId: string,
  ) {
    if (!isCurrentRequest(generation, runId)) return
    setEvents((current) =>
      isCurrentRequest(generation, runId) ? mergeRunEvents(current, page.events) : current,
    )
    latestSequenceRef.current = Math.max(
      latestSequenceRef.current,
      page.latestEventSequence,
      ...page.events.map((event) => event.sequence),
    )
  }

  useEffect(() => {
    const pendingCancel = readPendingCancelRun(agentId, storageRef.current)
    if (!pendingCancel) return
    const generation = generationRef.current + 1
    generationRef.current = generation
    currentRunIdRef.current = pendingCancel.runId
    latestSequenceRef.current = 0
    let cancelled = false

    getAgentRun(pendingCancel.runId)
      .then((restoredRun) => {
        if (
          cancelled ||
          generationRef.current !== generation ||
          currentRunIdRef.current !== pendingCancel.runId
        ) {
          return
        }
        const nextRun = withAcceptedCancelStatus(restoredRun, pendingCancel.accepted)
        if (nextRun.status === 'cancelled') settleCancelledRun(nextRun.runId)
        setRun(nextRun)
      })
      .catch((error) => {
        if (
          !cancelled &&
          generationRef.current === generation &&
          currentRunIdRef.current === pendingCancel.runId
        ) {
          setRequestError(`無法恢復取消中的 Run：${(error as Error).message}`)
        }
      })

    return () => {
      cancelled = true
    }
  }, [agentId, settleCancelledRun])

  useEffect(() => {
    if (!run?.runId || !ACTIVE_RUN_STATUSES.has(run.status)) return
    const runId = run.runId
    const generation = generationRef.current
    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | undefined

    async function poll() {
      try {
        const [nextRun, page] = await Promise.all([
          getAgentRun(runId),
          getAgentRunEvents(runId, latestSequenceRef.current),
        ])
        if (cancelled || !isCurrentRequest(generation, runId)) return
        const pendingCancel = readPendingCancelRun(agentId, storageRef.current)
        const displayedRun = withAcceptedCancelStatus(
          nextRun,
          pendingCancel?.runId === runId && pendingCancel.accepted,
        )
        if (displayedRun.status === 'cancelled') settleCancelledRun(runId)
        setRun(displayedRun)
        applyEventPage(page, generation, runId)
        setRequestError(null)
        if (ACTIVE_RUN_STATUSES.has(displayedRun.status)) {
          timer = setTimeout(() => void poll(), POLL_MS)
        }
      } catch (error) {
        if (!cancelled && isCurrentRequest(generation, runId)) {
          setRequestError(`Run 輪詢失敗：${(error as Error).message}`)
          timer = setTimeout(() => void poll(), POLL_MS)
        }
      }
    }

    void poll()
    return () => {
      cancelled = true
      if (timer) clearTimeout(timer)
    }
    // Status is intentional: a transition starts/stops the polling lifecycle.
  }, [agentId, run?.runId, run?.status, settleCancelledRun])

  async function start(forceNewAttempt = false) {
    const trimmed = message.trim()
    if (!trimmed || operation) return
    const attemptIdentity = [agentId, trimmed] as const
    const attempt = attemptFor(
      startAttemptRef,
      `${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:start:${encodeURIComponent(agentId)}`,
    )
    if (forceNewAttempt && !attempt.rotate(attemptIdentity)) return
    const idempotencyKey = attempt.keyFor(attemptIdentity)
    const previousRun = run
    const previousEvents = events
    const previousLatestSequence = latestSequenceRef.current
    const generation = generationRef.current + 1
    generationRef.current = generation
    currentRunIdRef.current = null
    latestSequenceRef.current = 0
    setRun(null)
    setEvents([])
    setOperation('starting')
    setStartFailed(false)
    setStartAmbiguous(attempt.isAmbiguous(attemptIdentity))
    setResumeFailed(false)
    setResumeAmbiguous(false)
    setRequestError(null)
    try {
      const started = await startAgentTestRun(agentId, trimmed, idempotencyKey)
      if (generationRef.current !== generation) return
      attempt.consume(attemptIdentity, idempotencyKey)
      setStartAmbiguous(false)
      currentRunIdRef.current = started.runId
      setRun(started)
      setResumeMessage('')
      toast(`已啟動測試 Run ${started.runId}`, 'success')
    } catch (error) {
      if (generationRef.current !== generation) return
      currentRunIdRef.current = previousRun?.runId ?? null
      latestSequenceRef.current = previousLatestSequence
      setRun(previousRun)
      setEvents(previousEvents)
      setStartFailed(true)
      if (isAmbiguousFailure(error)) {
        attempt.markAmbiguous(attemptIdentity, idempotencyKey)
        setStartAmbiguous(true)
      } else {
        attempt.consume(attemptIdentity, idempotencyKey)
        setStartAmbiguous(false)
      }
      setRequestError((error as Error).message)
    } finally {
      if (generationRef.current === generation) setOperation(null)
    }
  }

  async function refresh() {
    if (!run || operation) return
    const requestedRunId = run.runId
    const generation = generationRef.current
    setRequestError(null)
    try {
      const [nextRun, page] = await Promise.all([
        getAgentRun(requestedRunId),
        getAgentRunEvents(requestedRunId, latestSequenceRef.current),
      ])
      if (!isCurrentRequest(generation, requestedRunId)) return
      if (nextRun.status === 'cancelled') settleCancelledRun(requestedRunId)
      setRun(nextRun)
      applyEventPage(page, generation, requestedRunId)
    } catch (error) {
      if (!isCurrentRequest(generation, requestedRunId)) return
      setRequestError((error as Error).message)
    }
  }

  async function resume(forceNewAttempt = false) {
    const trimmed = resumeMessage.trim()
    if (
      !run ||
      run.status !== 'waiting_input' ||
      run.checkpointVersion === null ||
      !trimmed ||
      operation
    ) {
      return
    }
    const requestedRunId = run.runId
    const generation = generationRef.current
    const attemptIdentity = [requestedRunId, run.checkpointVersion, trimmed] as const
    const attempt = attemptFor(
      resumeAttemptRef,
      `${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:resume:${encodeURIComponent(requestedRunId)}:${run.checkpointVersion}`,
    )
    if (forceNewAttempt && !attempt.rotate(attemptIdentity)) return
    const idempotencyKey = attempt.keyFor(attemptIdentity)
    setOperation('resuming')
    setResumeFailed(false)
    setResumeAmbiguous(attempt.isAmbiguous(attemptIdentity))
    setRequestError(null)
    try {
      const resumed = await resumeAgentRun(
        requestedRunId,
        trimmed,
        run.checkpointVersion,
        idempotencyKey,
      )
      if (!isCurrentRequest(generation, requestedRunId)) return
      attempt.consume(attemptIdentity, idempotencyKey)
      setResumeAmbiguous(false)
      setRun(resumed)
      setResumeMessage('')
      toast('已送出補充資訊，Run 將從原 checkpoint 恢復。', 'success')
    } catch (error) {
      if (!isCurrentRequest(generation, requestedRunId)) return
      setResumeFailed(true)
      if (isAmbiguousFailure(error)) {
        attempt.markAmbiguous(attemptIdentity, idempotencyKey)
        setResumeAmbiguous(true)
      } else {
        attempt.consume(attemptIdentity, idempotencyKey)
        setResumeAmbiguous(false)
      }
      setRequestError((error as Error).message)
    } finally {
      if (isCurrentRequest(generation, requestedRunId)) setOperation(null)
    }
  }

  async function cancel() {
    if (!run || operation) return
    const requestedRunId = run.runId
    const generation = generationRef.current
    if (
      !(await confirm(`取消測試 Run「${requestedRunId}」？取消後不可 resume。`, {
        danger: true,
        confirmLabel: '取消 Run',
      }))
    ) {
      return
    }
    if (!isCurrentRequest(generation, requestedRunId)) return
    const attemptIdentity = [requestedRunId] as const
    const attempt = cancelAttemptFor(requestedRunId)
    const idempotencyKey = attempt.keyFor(attemptIdentity)
    writePendingCancelRun(
      agentId,
      { runId: requestedRunId, accepted: false },
      storageRef.current,
    )
    setOperation('cancelling')
    setRequestError(null)
    try {
      const cancelledRun = await cancelAgentRun(requestedRunId, idempotencyKey)
      if (!isCurrentRequest(generation, requestedRunId)) return
      const displayedRun = withAcceptedCancelStatus(cancelledRun, true)
      if (displayedRun.status === 'cancelled') {
        attempt.consume(attemptIdentity, idempotencyKey)
        clearPendingCancelRun(agentId, storageRef.current)
      } else {
        writePendingCancelRun(
          agentId,
          { runId: requestedRunId, accepted: true },
          storageRef.current,
        )
      }
      setRun(displayedRun)
      toast('已要求取消 Run。', 'success')
    } catch (error) {
      if (!isCurrentRequest(generation, requestedRunId)) return
      if (isAmbiguousFailure(error)) {
        attempt.markAmbiguous(attemptIdentity, idempotencyKey)
      } else {
        attempt.consume(attemptIdentity, idempotencyKey)
        clearPendingCancelRun(agentId, storageRef.current)
      }
      setRequestError((error as Error).message)
    } finally {
      if (isCurrentRequest(generation, requestedRunId)) setOperation(null)
    }
  }

  const canCancel =
    !!run &&
    !TERMINAL_RUN_STATUSES.has(run.status) &&
    run.status !== 'cancelling' &&
    operation === null
  const cancellationPending = run?.status === 'cancelling'

  return (
    <section className="agent-block agent-test-console" aria-busy={operation !== null}>
      <h4 className="agent-block__title">Direct Agent 測試主控台</h4>
      <p className="muted">
        對已發布的 Agent r{publishedRevision} 啟動 ADMIN 測試。此主控台只輪詢狀態，
        不使用串流、不提供核准，也不開放寫入工具。
      </p>
      {!enabled && (
        <p className="field-error" role="alert">
          Agent 已停用，無法開始新的測試 Run。
        </p>
      )}

      <div className="field">
        <label htmlFor="agent-test-message">測試訊息</label>
        <textarea
          id="agent-test-message"
          className="textarea"
          value={message}
          disabled={!enabled || operation !== null || cancellationPending}
          placeholder="輸入要交給這個已發布 Agent 的真實測試任務"
          onChange={(event) => setMessage(event.target.value)}
        />
      </div>
      <div className="agent-test-console__actions">
        <button
          className="btn btn--info"
          type="button"
          disabled={!enabled || operation !== null || cancellationPending || !message.trim()}
          onClick={() => void start()}
        >
          {operation === 'starting' ? '啟動中…' : '啟動測試 Run'}
        </button>
        {startFailed && (
          <button
            className="btn"
            type="button"
            disabled={
              !enabled ||
              operation !== null ||
              cancellationPending ||
              !message.trim() ||
              startAmbiguous
            }
            onClick={() => void start(true)}
          >
            以新嘗試重送 Start
          </button>
        )}
        {startFailed && startAmbiguous && (
          <span className="muted" role="status">
            結果尚未確定，請先用「啟動測試 Run」以相同 key 重試。
          </span>
        )}
        {run && (
          <>
            <button
              className="btn"
              type="button"
              disabled={operation !== null}
              onClick={() => void refresh()}
            >
              立即重新整理
            </button>
            <button
              className="btn btn--danger"
              type="button"
              disabled={!canCancel}
              onClick={() => void cancel()}
            >
              {operation === 'cancelling' ? '取消中…' : '取消 Run'}
            </button>
          </>
        )}
      </div>
      <ErrorText msg={requestError} />

      {run && (
        <div className="agent-test-console__run">
          <div className="agent-test-console__status" role="status" aria-live="polite">
            <span className="badge badge--user">{statusLabel(run.status)}</span>
            <code>{run.runId}</code>
          </div>

          <dl className="agent-test-console__summary">
            <div>
              <dt>Pinned Agent</dt>
              <dd>{run.pinnedAgentRevision === null ? '未回傳' : `r${run.pinnedAgentRevision}`}</dd>
            </div>
            <div>
              <dt>Pinned Workflow</dt>
              <dd>
                {run.pinnedWorkflowRevision === null ? '未回傳' : `r${run.pinnedWorkflowRevision}`}
              </dd>
            </div>
            <div>
              <dt>State / checkpoint</dt>
              <dd>
                {run.stateVersion ?? '—'} / {run.checkpointVersion ?? '—'}
              </dd>
            </div>
            <div>
              <dt>更新時間</dt>
              <dd>{run.updatedAt ? fmtDate(run.updatedAt) : '—'}</dd>
            </div>
          </dl>

          {run.pinnedSkills.length > 0 && (
            <>
              <h5>Pinned Skills</h5>
              <ul className="agent-test-console__pins">
                {run.pinnedSkills.map((skill) => (
                  <li key={`${skill.name}:${skill.revision}`}>
                    {skill.name} · {skill.revision === null ? 'revision 未回傳' : `r${skill.revision}`}
                  </li>
                ))}
              </ul>
            </>
          )}

          {Object.keys(run.budget).length > 0 && (
            <details>
              <summary>Budget 使用量</summary>
              <JsonValue value={run.budget} />
            </details>
          )}

          {run.status === 'waiting_input' && (
            <div className="agent-test-console__resume">
              <p role="status">
                {run.pendingInputMessage ?? '此 Run 需要使用者補充最小必要資訊後才能繼續。'}
              </p>
              {run.checkpointVersion === null && (
                <p className="field-error" role="alert">
                  回應缺少 checkpointVersion，為避免從錯誤狀態恢復，resume 已鎖定。
                </p>
              )}
              <label htmlFor="agent-test-resume">補充資訊</label>
              <textarea
                id="agent-test-resume"
                className="textarea"
                value={resumeMessage}
                disabled={operation !== null || run.checkpointVersion === null}
                onChange={(event) => setResumeMessage(event.target.value)}
              />
              <button
                className="btn btn--info"
                type="button"
                disabled={
                  operation !== null || run.checkpointVersion === null || !resumeMessage.trim()
                }
                onClick={() => void resume()}
              >
                {operation === 'resuming' ? '恢復中…' : '從 checkpoint 恢復'}
              </button>
              {resumeFailed && (
                <button
                  className="btn"
                  type="button"
                  disabled={
                    operation !== null ||
                    run.checkpointVersion === null ||
                    !resumeMessage.trim() ||
                    resumeAmbiguous
                  }
                  onClick={() => void resume(true)}
                >
                  以新嘗試重送 Resume
                </button>
              )}
              {resumeFailed && resumeAmbiguous && (
                <p className="muted" role="status">
                  結果尚未確定，請先以相同 key 重送 Resume。
                </p>
              )}
            </div>
          )}

          {run.status === 'waiting_approval' && (
            <p className="field-error" role="alert">
              此 Run 正在等待核准。D3 測試主控台不提供 approval 或寫入操作；可取消 Run。
            </p>
          )}
          {run.error && <ErrorText msg={run.error} />}
          {run.output !== undefined && (
            <details open={run.status === 'completed'}>
              <summary>Run 輸出</summary>
              <JsonValue value={run.output} />
            </details>
          )}

          <section aria-labelledby="agent-test-events-title">
            <h5 id="agent-test-events-title">Sanitized trace events</h5>
            {events.length === 0 ? (
              <p className="muted">尚無事件。</p>
            ) : (
              <ol className="agent-test-console__events">
                {events.map((event) => (
                  <li key={event.sequence}>
                    <div className="agent-test-console__event-head">
                      <code>#{event.sequence}</code>
                      <strong>{event.eventType}</strong>
                      <span className="muted">
                        {event.createdAt ? fmtDate(event.createdAt) : ''}
                      </span>
                    </div>
                    {event.payload !== undefined && <JsonValue value={event.payload} />}
                  </li>
                ))}
              </ol>
            )}
          </section>
        </div>
      )}
    </section>
  )
}
