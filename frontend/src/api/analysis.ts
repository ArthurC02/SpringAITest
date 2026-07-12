import { apiFetch } from './http'
import type { AnalysisSummary } from '../types'

export function getSummary(): Promise<AnalysisSummary> {
  return apiFetch<AnalysisSummary>('/api/analysis/summary')
}
