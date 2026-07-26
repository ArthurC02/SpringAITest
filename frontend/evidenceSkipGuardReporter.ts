import type { FullResult, Reporter, TestCase, TestResult } from '@playwright/test/reporter'

/**
 * Release-evidence gate must never report a hollow PASS.
 *
 * `streaming.evidence.spec.ts` / `session-tool.evidence.spec.ts` call `test.skip(...)` when
 * EVIDENCE_STREAM_CASES_JSON / EVIDENCE_TOOL_PROMPT are absent — deliberate, so the outer
 * `verify-copilot-shared-core-evidence.ps1` harness can read the JUnit skip count and turn it
 * into a BLOCKED gate result. But that skip -> BLOCKED translation lives ONLY in the PS1
 * script. Anyone who runs the documented `npm run test:evidence` directly (bypassing the PS1)
 * with services up but the evidence env vars unset gets Playwright's default exit code 0 with
 * zero real assertions made — a false green.
 *
 * This reporter closes that gap at the npm-script level: whenever the run would otherwise end
 * "passed" but contains any skipped test, we flip the overall status to "failed" so a bare
 * `npm run test:evidence` cannot lie about coverage.
 */
export default class EvidenceSkipGuardReporter implements Reporter {
  private readonly skipped: TestCase[] = []

  onTestEnd(test: TestCase, result: TestResult): void {
    if (result.status === 'skipped') this.skipped.push(test)
  }

  async onEnd(result: FullResult): Promise<{ status?: FullResult['status'] } | void> {
    if (this.skipped.length === 0 || result.status !== 'passed') return undefined

    console.error(
      `\n[evidence-gate] BLOCKED, not PASS: ${this.skipped.length} test(s) were skipped rather than run. ` +
        'This means required evidence fixtures or services were absent ' +
        '(e.g. EVIDENCE_STREAM_CASES_JSON / EVIDENCE_TOOL_PROMPT env vars), not that a test failed or is broken:',
    )
    for (const test of this.skipped) {
      const reason = test.annotations.find((annotation) => annotation.type === 'skip')?.description
      console.error(`  - ${test.titlePath().join(' > ')}${reason ? ` (${reason})` : ''}`)
    }
    console.error(
      '[evidence-gate] Forcing a non-zero exit code so a bare `npm run test:evidence` cannot report a false PASS.\n',
    )
    return { status: 'failed' }
  }
}
