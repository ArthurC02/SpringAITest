/** 逐字重複的錯誤列(error-text)共用外殼;msg 為 null 時不渲染。CSS class 不變。 */
export default function ErrorText({ msg }: { msg: string | null }) {
  return msg ? (
    <p className="error-text" role="alert">
      {msg}
    </p>
  ) : null
}
