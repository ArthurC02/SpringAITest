/** 逐字重複的錯誤列(error-text)共用外殼;msg 為 null 時不渲染。CSS class 不變。
 *  id 供欄位錯誤用 aria-describedby 指回來(選填,其餘呼叫端行為不變)。 */
export default function ErrorText({ msg, id }: { msg: string | null; id?: string }) {
  return msg ? (
    <p className="error-text" role="alert" id={id}>
      {msg}
    </p>
  ) : null
}
