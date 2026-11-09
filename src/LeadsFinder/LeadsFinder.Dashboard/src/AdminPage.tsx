import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api, type AdminOverview, type AdminUser, type GenerationBatch, type Page, type UpdateAdminUser, type User } from './api'
import NcrMap from './NcrMap'
import Pagination from './Pagination'
import './admin.css'

const count = (value: number) => value.toLocaleString('en-IN')
const date = (value: string | null) => value ? new Date(value).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' }) : '—'
const km = (meters: number) => `${(meters / 1000).toLocaleString('en-IN', { maximumFractionDigits: 1 })} km`

function AdminIcon({ type = 'users' }: { type?: 'users' | 'database' | 'check' | 'lock' | 'plus' | 'shield' }) {
  return <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{type === 'database' ? <><ellipse cx="12" cy="5" rx="9" ry="3" /><path d="M3 5v14c0 4 18 4 18 0V5M3 12c0 4 18 4 18 0" /></> : type === 'check' ? <path d="m5 12 4 4L19 6" /> : type === 'lock' ? <><rect x="5" y="10" width="14" height="11" rx="2" /><path d="M8 10V7a4 4 0 0 1 8 0v3M12 15v2" /></> : type === 'plus' ? <path d="M12 4v16M4 12h16" /> : type === 'shield' ? <><path d="M12 3 3 6v6c0 5 9 9 9 9s9-4 9-9V6l-9-3Z" /><path d="m8 12 3 3 5-6" /></> : <><circle cx="9" cy="8" r="4" /><path d="M2 21v-3a7 7 0 0 1 14 0v3M17 5a4 4 0 0 1 0 8M22 21v-3a7 7 0 0 0-3-6" /></>}</svg>
}
function Feedback({ message, error = false, retry }: { message: string; error?: boolean; retry?: () => void }) {
  return <div className={`alert ${error ? 'alert-error' : 'alert-success'}`} role={error ? 'alert' : 'status'}><span>{message}</span>{retry && <button type="button" onClick={retry}>Try again</button>}</div>
}
function AccountStatus({ user }: { user: AdminUser }) {
  return <span className={`admin-user-status account-${user.status}`}><i />{user.status === 'active' ? 'Active' : user.status === 'trial' ? 'Pending approval' : user.status === 'locked' ? 'Access locked' : 'Blocked'}</span>
}
function LimitDialog({ user, onClose, onSave }: { user: AdminUser; onClose: () => void; onSave: (update: UpdateAdminUser) => Promise<void> }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [quarantineLimit, setQuarantineLimit] = useState(String(user.quarantineLimit))
  const [cleanLimit, setCleanLimit] = useState(String(user.cleanLimit))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { const element = dialog.current; element?.showModal(); return () => element?.close() }, [])
  async function submit(event: FormEvent) {
    event.preventDefault(); setError(null)
    if (![quarantineLimit, cleanLimit].every(value => Number.isSafeInteger(Number(value)) && Number(value) >= 0 && Number(value) <= 10_000_000)) {
      setError('Enter a whole number between 0 and 1,00,00,000 for each limit.'); return
    }
    setBusy(true)
    try { await onSave({ approved: user.approved, blocked: user.blocked, quarantineLimit: Number(quarantineLimit), cleanLimit: Number(cleanLimit) }) }
    catch (problem) { setError((problem as Error).message) }
    finally { setBusy(false) }
  }
  return <dialog ref={dialog} className="record-dialog admin-limit-dialog" aria-labelledby="quota-title" onCancel={event => { event.preventDefault(); if (!busy) onClose() }}>
    <div className="panel-heading"><div><span className="eyebrow">ACCOUNT CAPACITY</span><h2 id="quota-title">Record limits</h2><p>{user.name} · {user.email}</p></div><button type="button" className="icon-button" aria-label="Close record limits" disabled={busy} onClick={onClose}>×</button></div>
    <form className="panel-body" onSubmit={submit}>
      <div className="admin-limit-current"><span>Currently in quarantine <strong>{count(user.quarantineCount)}</strong></span><span>Currently clean <strong>{count(user.cleanCount)}</strong></span></div>
      <label className="field"><span>Maximum quarantine records</span><input required autoFocus type="number" min="0" max="10000000" step="1" value={quarantineLimit} onChange={event => setQuarantineLimit(event.target.value)} /></label>
      <label className="field"><span>Maximum clean records</span><input required type="number" min="0" max="10000000" step="1" value={cleanLimit} onChange={event => setCleanLimit(event.target.value)} /></label>
      <p className="admin-form-note">These limits control how many records this account can hold in each table. A limit of zero prevents additions to that table.</p>
      {error && <Feedback message={error} error />}
      <div className="dialog-actions"><button type="button" className="button button-ghost" onClick={onClose} disabled={busy}>Cancel</button><button className="button button-primary" disabled={busy}>{busy ? 'Saving limits…' : 'Save limits'}</button></div>
    </form>
  </dialog>
}

function GenerationTable({ batches }: { batches: GenerationBatch[] }) {
  return <div className="table-scroll"><table className="data-table admin-batches-table"><thead><tr><th>Area</th><th>Rows added</th><th>Radius</th><th>Created by</th><th>Created</th></tr></thead><tbody>{batches.map(batch => <tr key={batch.id}><td><strong className="table-title">{batch.locality || 'Selected area'}</strong><span className="table-sub">{batch.latitude.toFixed(4)}, {batch.longitude.toFixed(4)}</span></td><td><strong>{count(batch.count)}</strong></td><td>{km(batch.radiusMeters)}</td><td>{batch.createdByName}</td><td><span className="table-date">{date(batch.createdAtUtc)}</span></td></tr>)}</tbody></table></div>
}

export default function AdminPage({ currentUser, onSelfUpdated }: { currentUser: User; onSelfUpdated: (user: User) => void }) {
  const [tab, setTab] = useState<'users' | 'catalog'>('users')
  const [overview, setOverview] = useState<AdminOverview | null>(null)
  const [users, setUsers] = useState<Page<AdminUser> | null>(null)
  const [batches, setBatches] = useState<GenerationBatch[]>([])
  const [page, setPage] = useState(1)
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [revision, setRevision] = useState(0)
  const [usersLoading, setUsersLoading] = useState(true)
  const [overviewError, setOverviewError] = useState<string | null>(null)
  const [usersError, setUsersError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busyUser, setBusyUser] = useState<string | null>(null)
  const [editUser, setEditUser] = useState<AdminUser | null>(null)
  const [pendingRevoke, setPendingRevoke] = useState<AdminUser | null>(null)
  const revokeWarning = useRef<HTMLDivElement>(null)
  const [latitude, setLatitude] = useState(28.6139)
  const [longitude, setLongitude] = useState(77.209)
  const [radius, setRadius] = useState(5000)
  const [rowCount, setRowCount] = useState('10000')
  const [generating, setGenerating] = useState(false)
  const [generation, setGeneration] = useState<GenerationBatch | null>(null)
  const [generationError, setGenerationError] = useState<string | null>(null)
  const centerInRegion = latitude >= 28.25 && latitude <= 29.05 && longitude >= 76.75 && longitude <= 77.75

  useEffect(() => { const timer = window.setTimeout(() => { setSearch(searchInput); setPage(1) }, 300); return () => window.clearTimeout(timer) }, [searchInput])
  useEffect(() => { if (pendingRevoke) revokeWarning.current?.scrollIntoView({ behavior: 'smooth', block: 'center' }) }, [pendingRevoke])
  useEffect(() => {
    let active = true
    let timer: number | undefined
    async function load() {
      const results = await Promise.allSettled([api.adminOverview(), api.generationBatches()])
      if (!active) return
      if (results[0].status === 'fulfilled') { setOverview(results[0].value); setOverviewError(null) }
      else setOverviewError((results[0].reason as Error).message)
      if (results[1].status === 'fulfilled') setBatches(results[1].value)
      else setOverviewError((results[1].reason as Error).message)
      timer = window.setTimeout(() => void load(), 5000)
    }
    void load()
    return () => { active = false; window.clearTimeout(timer) }
  }, [revision])
  useEffect(() => {
    let active = true
    let timer: number | undefined
    setUsersLoading(true)
    async function load() {
      try { const result = await api.adminUsers(search, page); if (active) { setUsers(result); setUsersError(null) } }
      catch (problem) { if (active) setUsersError((problem as Error).message) }
      finally { if (active) { setUsersLoading(false); timer = window.setTimeout(() => void load(), 5000) } }
    }
    void load()
    return () => { active = false; window.clearTimeout(timer) }
  }, [search, page, revision])

  async function updateUser(user: AdminUser, changes: Partial<UpdateAdminUser>, message: string) {
    setBusyUser(user.id); setActionError(null); setNotice(null)
    try {
      const result = await api.updateUser(user.id, { approved: user.approved, blocked: user.blocked, quarantineLimit: user.quarantineLimit, cleanLimit: user.cleanLimit, ...changes })
      setUsers(previous => previous ? { ...previous, items: previous.items.map(item => item.id === result.id ? result : item) } : previous)
      if (result.id === currentUser.id) onSelfUpdated(result)
      setNotice(message); setRevision(value => value + 1)
    } catch (problem) { setActionError((problem as Error).message); throw problem }
    finally { setBusyUser(null) }
  }
  function changeApproval(user: AdminUser) {
    if (user.approved && Date.now() - new Date(user.createdAtUtc).getTime() >= 10 * 86_400_000) { setPendingRevoke(user); return }
    void updateUser(user, { approved: !user.approved }, user.approved ? `Approval revoked for ${user.name}. Account age rules apply again.` : `${user.name} is approved and exempt from account age restrictions.`).catch(() => {})
  }
  async function generate(event: FormEvent) {
    event.preventDefault(); setGenerationError(null); setGeneration(null)
    if (!Number.isInteger(Number(rowCount)) || Number(rowCount) < 1 || Number(rowCount) > 1_000_000) { setGenerationError('Choose between 1 and 10,00,000 rows per batch.'); return }
    setGenerating(true)
    try { const result = await api.generateCatalog({ latitude, longitude, radiusMeters: radius, count: Number(rowCount) }); setGeneration(result); setRevision(value => value + 1) }
    catch (problem) { setGenerationError((problem as Error).message) }
    finally { setGenerating(false) }
  }

  return <div className="admin-page">
    <div className="page-heading"><div><span className="eyebrow">WORKSPACE MANAGEMENT</span><h1>Administration</h1><p>Manage account access, record capacity, and the shared data source.</p></div><span className="admin-access-badge"><AdminIcon type="shield" />Administrator access</span></div>
    {overviewError && <Feedback message={overviewError} error retry={() => setRevision(value => value + 1)} />}
    <section className="metrics-grid" aria-label="Administration overview">
      {[{ label: 'Total accounts', value: overview?.totalUsers, note: `${overview?.approvedUsers ?? 0} approved accounts`, type: 'users' as const, tone: 'blue' }, { label: 'Access restricted', value: (overview?.blockedUsers ?? 0) + (overview?.lockedUsers ?? 0), note: `${overview?.blockedUsers ?? 0} blocked · ${overview?.lockedUsers ?? 0} locked`, type: 'lock' as const, tone: 'amber' }, { label: 'Global records', value: overview?.globalRecords, note: 'Places saved to the shared database', type: 'database' as const, tone: 'teal' }, { label: 'Source rows', value: overview?.catalogSize, note: `${count(overview?.generatedRows ?? 0)} rows added through generation`, type: 'plus' as const, tone: 'violet' }].map(metric => <div className="metric" key={metric.label}><div className="metric-top"><span>{metric.label}</span><span className={`metric-icon ${metric.tone}`}><AdminIcon type={metric.type} /></span></div><strong>{metric.value === undefined ? '—' : count(metric.value)}</strong><p>{metric.note}</p></div>)}
    </section>
    <div className="admin-tabbar"><div className="segmented" aria-label="Administration sections"><button type="button" className={tab === 'users' ? 'selected' : ''} onClick={() => setTab('users')}><AdminIcon />User accounts</button><button type="button" className={tab === 'catalog' ? 'selected' : ''} onClick={() => setTab('catalog')}><AdminIcon type="database" />Data source</button></div><span>{count(overview?.totalJobs ?? 0)} search jobs across all accounts</span></div>
    {notice && <Feedback message={notice} />}{actionError && <Feedback message={actionError} error />}
    {pendingRevoke && <div ref={revokeWarning} className="admin-revoke-warning" role="alert"><div><strong>Revoking approval will make {pendingRevoke.name} eligible for deletion.</strong><p>This account is more than 10 days old. Its account, jobs, and private record history can be deleted as soon as approval is revoked. Shared places remain in the global database.</p></div><div className="admin-revoke-actions"><button type="button" className="button button-ghost small" disabled={busyUser === pendingRevoke.id} onClick={() => setPendingRevoke(null)}>Cancel</button><button type="button" className="button admin-block-button small" disabled={busyUser === pendingRevoke.id} onClick={() => void updateUser(pendingRevoke, { approved: false }, `Approval revoked for ${pendingRevoke.name}. The account is now eligible for deletion.`).then(() => setPendingRevoke(null)).catch(() => {})}>{busyUser === pendingRevoke.id ? 'Revoking…' : 'Confirm revoke approval'}</button></div></div>}
    {tab === 'users' ? <>
      <div className="admin-policy-note"><span className="soft-icon amber"><AdminIcon type="lock" /></span><div><strong>Approval keeps an account active.</strong><p>Unapproved accounts lock 3 days after registration and are deleted with their records after 10 days. Approved accounts are exempt from both rules. Blocking prevents access immediately.</p></div></div>
      <section className="panel"><div className="panel-heading"><div><h2>User accounts <span className="count-badge">{users?.total ?? 0}</span></h2><p>Approve access, block accounts, and set individual record limits.</p></div><button type="button" className="button button-ghost small" onClick={() => setRevision(value => value + 1)}>Refresh</button></div>
        <div className="table-toolbar"><label className="search-field"><AdminIcon /><input type="search" aria-label="Search user accounts" placeholder="Search by name or email…" value={searchInput} onChange={event => setSearchInput(event.target.value)} /></label><span className="admin-filter-note">Account settings take effect immediately.</span></div>
        {usersError && <Feedback message={usersError} error retry={() => setRevision(value => value + 1)} />}
        {!users && usersLoading ? <div className="loading-state" role="status"><span className="spinner" />Loading accounts…</div> : users?.items.length ? <>
          <div className="table-scroll"><table className="data-table admin-users-table"><thead><tr><th>Account</th><th>Access</th><th>Record capacity</th><th>Account dates</th><th>Controls</th></tr></thead><tbody>{users.items.map(user => <tr key={user.id}><td><strong className="table-title">{user.name}{user.id === currentUser.id && <span className="admin-you">You</span>}</strong><span className="table-sub">{user.email}</span><span className={`admin-role role-${user.role}`}>{user.role === 'admin' ? 'Administrator' : 'User'}</span><span className="table-sub">{count(user.totalJobs)} search jobs</span></td><td><AccountStatus user={user} /><span className={user.approved ? 'admin-approval approved' : 'admin-approval'}>{user.approved ? 'Approved' : 'Awaiting approval'}</span>{!user.approved && user.deletionAtUtc && <span className="admin-delete-date">Deletion: {date(user.deletionAtUtc)}</span>}</td><td><div className="admin-capacity-row"><span>Quarantine</span><strong>{count(user.quarantineCount)} <em>/ {count(user.quarantineLimit)}</em></strong></div><div className="admin-capacity-row"><span>Clean</span><strong>{count(user.cleanCount)} <em>/ {count(user.cleanLimit)}</em></strong></div><button type="button" className="admin-text-button" onClick={() => setEditUser(user)}>Edit record limits</button></td><td><span className="admin-date-label">Registered</span><span className="table-date">{date(user.createdAtUtc)}</span><span className="admin-date-label">Last sign in</span><span className="table-date">{date(user.lastLoginAtUtc)}</span>{!user.approved && <><span className="admin-date-label">Access ends</span><span className="table-date">{date(user.trialEndsAtUtc)}</span></>}</td><td><div className="admin-user-actions"><button type="button" className={`button small ${user.approved ? 'button-ghost' : 'button-primary'}`} disabled={busyUser === user.id || user.id === currentUser.id || user.role === 'admin'} onClick={() => changeApproval(user)}>{busyUser === user.id ? 'Updating…' : user.approved ? 'Revoke approval' : 'Approve account'}</button><button type="button" className={`button small ${user.blocked ? 'button-ghost' : 'admin-block-button'}`} disabled={busyUser === user.id || user.id === currentUser.id || user.role === 'admin'} onClick={() => void updateUser(user, { blocked: !user.blocked }, user.blocked ? `${user.name} is unblocked.` : `${user.name} is blocked and cannot access the workspace.`).catch(() => {})}>{user.blocked ? 'Unblock account' : 'Block account'}</button></div></td></tr>)}</tbody></table></div>
          <Pagination result={users} onChange={setPage} loading={usersLoading} />
        </> : <div className="empty-state"><span className="empty-icon"><AdminIcon /></span><h3>No accounts match your search</h3><p>Try another name or email address.</p></div>}
      </section>
    </> : <>
      <div className="admin-generator-grid"><section className="panel map-panel"><div className="panel-heading"><div><h2>Choose a generation area</h2><p>Choose a location in Delhi NCR or enter coordinates within this region.</p></div><span className="region-badge">Delhi NCR</span></div>{centerInRegion ? <NcrMap latitude={latitude} longitude={longitude} radiusMeters={radius} onCenterChange={(lat, lon) => { setLatitude(lat); setLongitude(lon) }} onRadiusChange={setRadius} /> : <div className="empty-state"><span className="empty-icon"><AdminIcon type="database" /></span><h3>Choose coordinates within Delhi NCR</h3><p>Latitude 28.25–29.05 and longitude 76.75–77.75 keep generated hotels available in the discovery region.</p><button type="button" className="button button-ghost small" onClick={() => { setLatitude(28.6139); setLongitude(77.209) }}>Return to New Delhi</button></div>}</section><section className="panel"><div className="panel-heading"><div><span className="eyebrow">EXPAND THE SHARED SOURCE</span><h2>Generate sample hotel records</h2><p>Add sample businesses for demo searches.</p></div></div><form className="panel-body" onSubmit={generate}><div className="field-row"><label className="field"><span>Latitude</span><input required type="number" min="28.25" max="29.05" step="any" value={latitude} onChange={event => setLatitude(Number(event.target.value))} /></label><label className="field"><span>Longitude</span><input required type="number" min="76.75" max="77.75" step="any" value={longitude} onChange={event => setLongitude(Number(event.target.value))} /></label></div><label className="field"><span>Radius in meters</span><input required type="number" min="500" max="50000" step="1" value={radius} onChange={event => setRadius(Number(event.target.value))} /></label><label className="field"><span>Place type</span><select aria-label="Generated place type" value="hotel" disabled><option value="hotel">Hotels</option></select></label><label className="field"><span>Rows to generate <span className="field-tag">Max 10,00,000</span></span><input required type="number" min="1" max="1000000" step="1" value={rowCount} onChange={event => setRowCount(event.target.value)} /><small>Each hotel starts with half of its address and contact fields empty.</small></label><div className="inline-note"><AdminIcon type="database" /><span>New rows enter the shared source. Users can discover them through searches in this area.</span></div>{generationError && <Feedback message={generationError} error />}<button className="button button-primary full-width" disabled={generating}>{generating ? <><span className="spinner" />Generating records…</> : <><AdminIcon type="plus" />Generate sample hotel records</>}</button></form></section></div>
      {generation && <div className="admin-generation-result" role="status"><span className="soft-icon"><AdminIcon type="check" /></span><div><strong>{count(generation.count)} hotel records added</strong><p>{generation.locality || 'Selected area'} · {km(generation.radiusMeters)} around {generation.latitude.toFixed(4)}, {generation.longitude.toFixed(4)}</p><span>Available to all users in the shared source.</span></div></div>}
      <section className="panel admin-batches-panel"><div className="panel-heading"><div><h2>Generation history <span className="count-badge">{batches.length}</span></h2><p>Recent additions to the shared catalog, with their geographic coverage.</p></div><span className="admin-catalog-total">{count(overview?.generatedRows ?? 0)} generated rows</span></div>{batches.length ? <GenerationTable batches={batches} /> : <div className="empty-state"><span className="empty-icon"><AdminIcon type="database" /></span><h3>No generation batches yet</h3><p>Choose an area and a row count to grow the shared hotel source.</p></div>}</section>
    </>}
    {editUser && <LimitDialog user={editUser} onClose={() => setEditUser(null)} onSave={async update => { await updateUser(editUser, update, `Record limits updated for ${editUser.name}.`); setEditUser(null) }} />}
  </div>
}
