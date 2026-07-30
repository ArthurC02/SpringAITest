/** null = 尚無觀測資料;一律顯示明確的「未知」badge,絕不假裝成 0。
 * Shared by OperationsGovernanceView and EvaluationPanel to break circular import. */
export function Observed({ value, unit = '' }: { value: number | string | null; unit?: string }) {
  return value === null ? (
    <span className="chip chip--warn" title="尚無觀測資料,顯示未知而非假裝精確的 0">
      未知
    </span>
  ) : (
    <>
      {value}
      {unit}
    </>
  )
}
