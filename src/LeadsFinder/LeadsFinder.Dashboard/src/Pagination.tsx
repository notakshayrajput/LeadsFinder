import { useEffect, useState, type FormEvent } from 'react'
import type { Page } from './api'

export default function Pagination({ result, onChange, loading }: { result: Page<unknown>; onChange: (page: number) => void; loading?: boolean }) {
  const [draft, setDraft] = useState(String(result.page))
  const pages = Math.max(1, Math.ceil(result.total / result.pageSize))
  useEffect(() => setDraft(String(result.page)), [result.page, pages])
  function submit(event: FormEvent) {
    event.preventDefault()
    const value = Number(draft)
    if (Number.isInteger(value) && value >= 1 && value <= pages) onChange(value)
  }
  if (!result.total) return null
  return <div className="pagination">
    <span>{((result.page - 1) * result.pageSize + 1).toLocaleString('en-IN')}–{Math.min(result.page * result.pageSize, result.total).toLocaleString('en-IN')} of <strong>{result.total.toLocaleString('en-IN')}</strong></span>
    <div className="pagination-controls">
      <button className="button button-ghost small" aria-label="First page" disabled={result.page <= 1 || loading} onClick={() => onChange(1)}>«</button>
      <button className="button button-ghost small" disabled={result.page <= 1 || loading} onClick={() => onChange(result.page - 1)}>Previous</button>
      <form onSubmit={submit} className="page-jump"><label>Page <input aria-label="Page number" type="number" inputMode="numeric" min={1} max={pages} step={1} required value={draft} disabled={loading} onChange={event => setDraft(event.target.value)} /></label><span>of {pages.toLocaleString('en-IN')}</span><button className="button button-ghost small" disabled={loading}>Go</button></form>
      <button className="button button-ghost small" disabled={result.page >= pages || loading} onClick={() => onChange(result.page + 1)}>Next</button>
      <button className="button button-ghost small" aria-label="Last page" disabled={result.page >= pages || loading} onClick={() => onChange(pages)}>»</button>
    </div>
  </div>
}
