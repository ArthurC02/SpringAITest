import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import Modal from '../src/components/Modal'

export function mountModalHarness() {
  const host = document.createElement('div')
  document.body.replaceChildren(host)

  function Harness() {
    const [open, setOpen] = useState(false)
    const [busy, setBusy] = useState(false)
    return (
      <>
        <button data-testid="trigger" disabled={busy} onClick={() => setOpen(true)}>
          Open
        </button>
        <button onClick={() => setBusy(false)}>Release</button>
        <Modal open={open} busy={busy} onClose={() => setOpen(false)}>
          <button
            onClick={() => {
              setBusy(true)
              setOpen(false)
            }}
          >
            Finish
          </button>
        </Modal>
      </>
    )
  }

  createRoot(host).render(<Harness />)
}
