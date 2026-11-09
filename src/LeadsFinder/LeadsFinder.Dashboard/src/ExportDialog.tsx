import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api, type RecordFilters } from './api'

export default function ExportDialog({ filters, onClose }: { filters: RecordFilters; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [samples, setSamples] = useState(true)
  const [dnd, setDnd] = useState(filters.dnd === 'only')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { const element = dialog.current; element?.showModal(); return () => element?.close() }, [])
  async function download(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(null)
    try {
      const blob = await api.exportRecords({ ...filters, dnd: dnd ? filters.dnd === 'only' ? 'only' : 'all' : 'exclude' }, samples)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a'); link.href = url; link.download = `dynamics-sales-leads-${new Date().toLocaleDateString('en-CA')}.csv`
      document.body.appendChild(link); link.click(); link.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 1000)
      onClose()
    } catch (problem) { setError((problem as Error).message) }
    finally { setBusy(false) }
  }
  return <dialog ref={dialog} className="record-dialog export-dialog" aria-labelledby="export-title" onCancel={event => { event.preventDefault(); if (!busy) onClose() }}>
    <div className="panel-heading"><h2 id="export-title">Export to Dynamics 365 Sales</h2><button className="icon-button" disabled={busy} aria-label="Close export" onClick={onClose}>×</button></div>
    <form className="panel-body" onSubmit={download}><p>Export all records matching your filters, across every page. Duplicate businesses are combined into one row.</p>
      <label className="export-check"><input type="checkbox" checked={samples} onChange={event => setSamples(event.target.checked)} />Include sample records</label>
      <label className="export-check"><input type="checkbox" checked={dnd} onChange={event => setDnd(event.target.checked)} />Include DND records with contact restrictions</label>
      <p className="export-mapping">CSV columns match standard Sales lead fields. For these business prospects, the company name fills the required Last Name field. Replace it with a contact’s surname when available. Sales stage and contact history are included in Description; review mappings during import.</p>
      {error && <div className="alert alert-error" role="alert">{error}</div>}<div className="dialog-actions"><button type="button" className="button button-ghost" disabled={busy} onClick={onClose}>Cancel</button><button className="button button-primary" disabled={busy}>{busy ? 'Preparing…' : 'Download CSV'}</button></div>
    </form>
  </dialog>
}
