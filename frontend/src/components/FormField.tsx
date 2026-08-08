interface Props {
  id: string
  label: string
  value: string
  onChange: (val: string) => void
  error?: string
  testId?: string
  type?: string
  placeholder?: string
  autoComplete?: string
  minLength?: number
  required?: boolean
  onBlur?: (val: string) => void
}

/** 表單單一欄位（label + input + field-error）。有錯時以 `${id}-err` 建立 aria-describedby 關聯。 */
export default function FormField({
  id,
  label,
  value,
  onChange,
  error,
  testId,
  type = 'text',
  placeholder,
  autoComplete,
  minLength,
  required,
  onBlur,
}: Props) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        type={type}
        data-testid={testId}
        className="input"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onBlur={onBlur ? (e) => onBlur(e.target.value) : undefined}
        placeholder={placeholder}
        autoComplete={autoComplete}
        required={required}
        minLength={minLength}
        aria-invalid={!!error}
        aria-describedby={error ? `${id}-err` : undefined}
      />
      {error && (
        <span className="field-error" id={`${id}-err`} role="alert">
          {error}
        </span>
      )}
    </div>
  )
}
