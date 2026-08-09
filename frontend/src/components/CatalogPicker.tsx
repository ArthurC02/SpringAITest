import { useState } from 'react'

interface CatalogPickerProps<T> {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  disabled?: boolean
  /** null = 尚未載入完成或載入失敗;非 null(含空陣列)= 目錄可用。 */
  items: T[] | null
  itemsError: string | null
  itemsLoading: boolean
  optionValue: (item: T) => string
  optionLabel: (item: T) => string
  placeholder?: string
  hint?: string
  invalid?: boolean
  invalidHint?: string
}

/**
 * W5 裸 JSON/GUID 清零共用元件:目錄載入成功且非空時預設用下拉選單;目錄載入失敗
 * (例如對應功能旗標關閉導致 404)、仍在載入、或目錄本身是空清單,一律優雅退回手動
 * 輸入框——不壞頁、不擋住既有手動輸入動線。載入成功的目錄仍保留「改為手動輸入」
 * 出口(例如目標項目因租戶/分頁限制未出現在清單中)。伺服器端驗證/授權才是最終
 * 權威,這裡的下拉只是減少手誤的 UX 提示。
 */
export default function CatalogPicker<T>({
  id,
  label,
  value,
  onChange,
  disabled,
  items,
  itemsError,
  itemsLoading,
  optionValue,
  optionLabel,
  placeholder,
  hint,
  invalid,
  invalidHint,
}: CatalogPickerProps<T>) {
  const catalogAvailable = !itemsError && !!items && items.length > 0
  const [manual, setManual] = useState(false)
  const useManual = manual || !catalogAvailable

  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {hint && <p className="muted agent-set__hint">{hint}</p>}
      {itemsLoading && <p className="muted">清單載入中…</p>}
      {itemsError && <p className="muted">清單載入失敗,已切換為手動輸入。</p>}
      {useManual ? (
        <input
          id={id}
          className="input"
          value={value}
          placeholder={placeholder}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={invalid}
          aria-describedby={invalid ? `${id}-err` : undefined}
        />
      ) : (
        <select
          id={id}
          className="input"
          value={value}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
        >
          <option value="">請選擇…</option>
          {items!.map((item) => (
            <option key={optionValue(item)} value={optionValue(item)}>
              {optionLabel(item)}
            </option>
          ))}
        </select>
      )}
      {catalogAvailable && !itemsLoading && !disabled && (
        <button type="button" className="btn" onClick={() => setManual((m) => !m)}>
          {useManual ? '改用清單選擇' : '改為手動輸入'}
        </button>
      )}
      {invalid && invalidHint && (
        <span className="field-error" id={`${id}-err`} role="alert">
          {invalidHint}
        </span>
      )}
    </div>
  )
}
