import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api, normalizeLead, type Lead, type ReviewField, type SalesProfile } from './api'

export const salesStages = { new: 'New', in_progress: 'In progress', possible_sale: 'Possible sale', won: 'Won', lost: 'Lost' }
const telephone = (value: string | null) => {
  const digits = (value ?? '').replace(/\D/g, '')
  if (digits.startsWith('9100000') || digits.length < 10 || digits.length > 15) return null
  return digits.length === 10 ? `91${digits}` : digits
}
const emailAddress = (value: string | null) => value && !value.toLowerCase().endsWith('.example') && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value) ? value : null
const websiteUrl = (value: string | null) => {
  try { const url = new URL(value ?? ''); return ['http:', 'https:'].includes(url.protocol) && !url.hostname.endsWith('.example') ? url.href : null } catch { return null }
}

function ContactActions({ lead }: { lead: Lead }) {
  const phone = telephone(lead.phone), email = emailAddress(lead.email)
  const blocked = lead.sales.doNotContact
  const reason = blocked ? 'Do not contact is enabled' : 'Sample or missing contact — add a real contact to use this action'
  return <div className="contact-actions">
    {phone && !blocked ? <a href={`tel:+${phone}`} className="contact-action">Call</a> : <button disabled title={reason} className="contact-action">Call</button>}
    {phone && !blocked ? <a href={`https://wa.me/${phone}`} target="_blank" rel="noreferrer" className="contact-action">WhatsApp</a> : <button disabled title={reason} className="contact-action">WhatsApp</button>}
    {email && !blocked ? <a href={`mailto:${encodeURIComponent(email)}`} className="contact-action">Email</a> : <button disabled title={reason} className="contact-action">Email</button>}
  </div>
}

function FieldReview({ lead, field, onUpdated }: { lead: Lead; field: ReviewField; onUpdated: (lead: Lead) => void }) {
  const quality = lead.fieldQuality[field]
  const value = lead[field]
  const [reporting, setReporting] = useState(false)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function save(changes: { confidence?: string; inaccurate?: boolean; reason?: string }) {
    if (!value) return
    setBusy(true); setError(null)
    try { onUpdated(await api.reviewField(lead.id, { field, value, ...changes })); setReporting(false) }
    catch (problem) { setError((problem as Error).message) }
    finally { setBusy(false) }
  }
  return <div className="quality-field">
    <div className="quality-value"><strong>{field === 'phone' ? 'Phone' : field[0].toUpperCase() + field.slice(1)}</strong><span>{value || 'Not available'}</span></div>
    {value && <><div className="quality-controls"><label>My confidence<select aria-label={`${field} confidence`} value={quality?.confidence ?? ''} disabled={busy} onChange={event => void save({ confidence: event.target.value })}><option value="">Unrated</option><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option></select></label><button type="button" disabled={busy} className={`button button-ghost small ${quality?.reportedByMe ? 'reported' : ''}`} onClick={() => quality?.reportedByMe ? void save({ inaccurate: false }) : setReporting(!reporting)}>{quality?.reportedByMe ? 'Withdraw report' : 'Report inaccurate'}</button></div>
    {!!quality?.inaccurateReports && <p className="field-warning" role="status">{quality.inaccurateReports} {quality.inaccurateReports === 1 ? 'user reported' : 'users reported'} this value as inaccurate{quality.communitySignal === 'low' ? ' · Low community confidence' : ''}.</p>}
    {reporting && <div className="field-report"><textarea maxLength={500} aria-label={`Reason for reporting ${field}`} placeholder="What is incorrect? (optional)" value={reason} onChange={event => setReason(event.target.value)} /><button type="button" className="button button-primary small" disabled={busy} onClick={() => void save({ inaccurate: true, reason })}>Save report</button></div>}</>}
    {error && <p className="field-warning" role="alert">{error}</p>}
  </div>
}

function LeadDialog({ initial, onClose, onUpdated }: { initial: Lead; onClose: () => void; onUpdated: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [lead, setLead] = useState(initial)
  const [sales, setSales] = useState<SalesProfile>(initial.sales)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  useEffect(() => { const element = dialog.current; element?.showModal(); return () => element?.close() }, [])
  async function save(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(null); setSaved(false)
    try { const updated = await api.updateSales(lead.id, sales); setLead(updated); setSales(updated.sales); setSaved(true); onUpdated() }
    catch (problem) { setError((problem as Error).message) }
    finally { setBusy(false) }
  }
  return <dialog ref={dialog} className="lead-dialog" onCancel={event => { event.preventDefault(); if (!busy) onClose() }} aria-labelledby="lead-dialog-title">
    <div className="panel-heading"><div><h2 id="lead-dialog-title">{lead.name}</h2><p>{lead.locality}{lead.isSample ? ' · Sample record' : ' · Manually added'}</p></div><button className="icon-button" aria-label="Close lead details" disabled={busy} onClick={onClose}>×</button></div>
    <div className="lead-dialog-body"><ContactActions lead={{ ...lead, sales: { ...sales, doNotContact: lead.sales.doNotContact || sales.doNotContact } }} /><p className="contact-hint">Opening an app does not mark this lead as contacted.</p>
      <form className="sales-form" onSubmit={save} onChange={() => setSaved(false)}>
        <label className="field"><span>Sales stage</span><select value={sales.stage} onChange={event => setSales({ ...sales, stage: event.target.value as SalesProfile['stage'] })}>{Object.entries(salesStages).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <fieldset className="contact-flags"><legend>Contacted on</legend>{([['contactedOnEmail', 'Email'], ['contactedOnPhone', 'Phone'], ['contactedOnWhatsapp', 'WhatsApp']] as const).map(([field, label]) => <label key={field}><input type="checkbox" checked={sales[field]} onChange={event => setSales({ ...sales, [field]: event.target.checked })} />{label}</label>)}</fieldset>
        <label className="dnd-control"><input type="checkbox" checked={sales.doNotContact} onChange={event => setSales({ ...sales, doNotContact: event.target.checked })} />Do not contact (DND)</label>
        <label className="field"><span>Notes</span><textarea maxLength={4000} rows={3} value={sales.notes} onChange={event => setSales({ ...sales, notes: event.target.value })} placeholder="Conversation notes or next step" /></label>
        <div className="sales-save"><button className="button button-primary" disabled={busy}>{busy ? 'Saving…' : 'Save activity'}</button>{saved && <span role="status">Saved</span>}</div>{error && <p role="alert" className="field-warning">{error}</p>}
      </form>
      <h3 className="quality-heading">Field accuracy</h3>
      {(['phone', 'email', 'website', 'address'] as ReviewField[]).map(field => <FieldReview key={field} lead={lead} field={field} onUpdated={updated => { setLead(updated); onUpdated() }} />)}
      <div className="lead-detail-footer"><span>{lead.status === 'clean' ? 'Enriched' : 'Quarantine'} · Found {new Date(lead.foundAtUtc).toLocaleDateString('en-IN')}</span><a href={`/jobs/${lead.jobId}`}>Open search job →</a></div>
    </div>
  </dialog>
}

export default function LeadTable({ leads, onUpdated }: { leads: Lead[]; onUpdated?: () => void }) {
  const [selected, setSelected] = useState<Lead | null>(null)
  const [pending, setPending] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [changes, setChanges] = useState<Record<string, Lead>>({})
  useEffect(() => setChanges({}), [leads])
  async function stage(lead: Lead, value: SalesProfile['stage']) {
    setPending(lead.id); setError(null)
    try { const updated = await api.updateSales(lead.id, { stage: value }); setChanges(previous => ({ ...previous, [lead.id]: updated })); onUpdated?.() }
    catch (problem) { setError((problem as Error).message) }
    finally { setPending(null) }
  }
  return <>{error && <div className="alert alert-error" role="alert">{error}</div>}<div className="table-scroll"><table className="data-table sales-table"><thead><tr><th>Business</th><th>Phone</th><th>Email</th><th>Sales stage</th><th>Contact</th><th><span className="sr-only">Details</span></th></tr></thead><tbody>{leads.map(original => {
    const lead = normalizeLead(changes[original.id] ?? original)
    const website = websiteUrl(lead.website)
    const reports = Object.values(lead.fieldQuality).reduce((sum, quality) => sum + quality.inaccurateReports, 0)
    return <tr key={lead.id} className={lead.sales.doNotContact ? 'dnd-row' : ''}>
      <td><button className="business-name" onClick={() => setSelected(lead)}>{lead.name}</button><span className="table-sub">{lead.locality}{website && <> · <a href={website} target="_blank" rel="noreferrer">Website ↗</a></>}</span>{reports > 0 && <button className="accuracy-alert" onClick={() => setSelected(lead)}>{reports} field {reports === 1 ? 'report' : 'reports'}</button>}</td>
      <td className="phone-cell">{lead.phone || '—'}</td><td className="email-cell">{lead.email || '—'}</td>
      <td><select className="stage-select" aria-label={`Sales stage for ${lead.name}`} disabled={pending === lead.id} value={lead.sales.stage} onChange={event => void stage(lead, event.target.value as SalesProfile['stage'])}>{Object.entries(salesStages).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>{lead.sales.doNotContact && <span className="dnd-label">DND</span>}</td>
      <td><ContactActions lead={lead} />{(lead.sales.contactedOnEmail || lead.sales.contactedOnPhone || lead.sales.contactedOnWhatsapp) && <span className="table-sub">Contacted: {[lead.sales.contactedOnEmail && 'email', lead.sales.contactedOnPhone && 'phone', lead.sales.contactedOnWhatsapp && 'WhatsApp'].filter(Boolean).join(', ')}</span>}</td>
      <td><button className="button button-ghost small" onClick={() => setSelected(lead)}>Details</button></td>
    </tr>
  })}</tbody></table></div>{selected && <LeadDialog initial={selected} onClose={() => setSelected(null)} onUpdated={() => onUpdated?.()} />}</>
}
