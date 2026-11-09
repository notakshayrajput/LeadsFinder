import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { api, ApiError, type Job, type JobStatus, type Lead, type RecordFilters, type User } from './api'
import NcrMap from './NcrMap'
import AdminPage from './AdminPage'
import Pagination from './Pagination'
import LeadTable, { salesStages } from './LeadTable'
import ExportDialog from './ExportDialog'

const placeTypes: Record<string, string> = { any_lodging: 'All lodging', hotel: 'Hotels', guest_house: 'Guest houses', hostel: 'Hostels', apartment: 'Apartments' }
const number = (value: number) => value.toLocaleString('en-IN')
const date = (value: string | null) => value ? new Date(value).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' }) : '—'
const distance = (value: number) => value >= 1000 ? `${number(value / 1000)} km` : `${value} m`
const isActive = (job: Job) => job.status === 'fetching' || job.status === 'enriching'

function Icon({ name, size = 20 }: { name: string; size?: number }) {
  const paths: Record<string, ReactNode> = {
    grid: <><rect x="3" y="3" width="7" height="7" rx="1.5" /><rect x="14" y="3" width="7" height="7" rx="1.5" /><rect x="3" y="14" width="7" height="7" rx="1.5" /><rect x="14" y="14" width="7" height="7" rx="1.5" /></>,
    compass: <><circle cx="12" cy="12" r="9" /><path d="m16 8-3 5-5 3 3-5 5-3Z" /></>,
    jobs: <><rect x="3" y="6" width="18" height="15" rx="2" /><path d="M8 6V3h8v3M3 12h18M10 12v3h4v-3" /></>,
    rows: <><rect x="3" y="3" width="18" height="18" rx="2" /><path d="M3 9h18M3 15h18M9 3v18" /></>,
    shield: <><path d="M12 3 3 6v6c0 5 9 9 9 9s9-4 9-9V6l-9-3Z" /><path d="m8 12 3 3 5-6" /></>,
    spark: <><path d="m12 3 2.7 6.3L21 12l-6.3 2.7L12 21l-2.7-6.3L3 12l6.3-2.7L12 3Z" /><path d="m20 2 .7 1.3L22 4l-1.3.7L20 6l-.7-1.3L18 4l1.3-.7L20 2Z" /></>,
    chart: <><path d="M3 3v18h18M7 16l4-5 4 2 6-8" /><path d="M17 5h4v4" /></>,
    search: <><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></>,
    arrow: <path d="M4 12h16m-6-6 6 6-6 6" />,
    plus: <path d="M12 5v14M5 12h14" />,
    check: <path d="m5 12 4 4L19 6" />,
    clock: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
    pin: <><path d="M20 10c0 6-8 11-8 11S4 16 4 10a8 8 0 1 1 16 0Z" /><circle cx="12" cy="10" r="2.5" /></>,
    logout: <><path d="M9 3H4v18h5m7-14 5 5-5 5M9 12h12" /></>,
    refresh: <><path d="M20 7v5h-5M4 17v-5h5" /><path d="M5 8a8 8 0 0 1 13-3l2 2M4 17l2 2a8 8 0 0 0 13-3" /></>,
    close: <path d="m6 6 12 12M6 18 18 6" />,
    phone: <path d="M5 3h4l2 5-3 2a16 16 0 0 0 6 6l2-3 5 2v4a2 2 0 0 1-2 2C10 21 3 14 3 5a2 2 0 0 1 2-2Z" />,
    mail: <><rect x="3" y="5" width="18" height="14" rx="2" /><path d="m3 6 9 7 9-7" /></>,
    external: <><path d="M14 3h7v7M21 3l-9 9" /><path d="M10 3H3v18h18v-7" /></>,
    menu: <path d="M4 6h16M4 12h16M4 18h16" />,
  }
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name] ?? paths.rows}</svg>
}

function navigate(path: string) { window.history.pushState({}, '', path); window.dispatchEvent(new PopStateEvent('popstate')); window.scrollTo(0, 0) }
function Link({ to, className, children, onClick }: { to: string; className?: string; children: ReactNode; onClick?: () => void }) {
  return <a href={to} className={className} onClick={event => { if (!event.ctrlKey && !event.metaKey && !event.shiftKey && event.button === 0) { event.preventDefault(); onClick?.(); navigate(to) } }}>{children}</a>
}

function useQuery<T>(loader: () => Promise<T>, dependencies: unknown[] = [], pollMs = 0) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [revision, setRevision] = useState(0)
  useEffect(() => {
    let active = true
    let timer: number | undefined
    setLoading(true)
    async function load() {
      try { const result = await loader(); if (active) { setData(result); setError(null) } }
      catch (problem) { if (active) setError((problem as Error).message) }
      finally { if (active) { setLoading(false); if (pollMs) timer = window.setTimeout(() => void load(), pollMs) } }
    }
    void load()
    return () => { active = false; window.clearTimeout(timer) }
  }, [...dependencies, revision, pollMs])
  return { data, error, loading, refresh: () => setRevision(value => value + 1) }
}

function Status({ status }: { status: JobStatus | Lead['status'] }) {
  const labels = { fetching: 'Finding records', quarantined: 'Ready to enrich', enriching: 'Enriching', completed: 'Completed', failed: 'Failed', quarantine: 'Quarantine', clean: 'Enriched' }
  return <span className={`status status-${status}`}><span className={status === 'fetching' || status === 'enriching' ? 'status-dot pulse' : 'status-dot'} />{labels[status]}</span>
}
function ErrorMessage({ children, retry }: { children: ReactNode; retry?: () => void }) {
  return <div className="alert alert-error" role="alert"><span>{children}</span>{retry && <button type="button" onClick={retry}>Try again</button>}</div>
}
function Empty({ icon = 'search', title, children, action }: { icon?: string; title: string; children: ReactNode; action?: ReactNode }) {
  return <div className="empty-state"><span className="empty-icon"><Icon name={icon} size={27} /></span><h3>{title}</h3><p>{children}</p>{action}</div>
}
function Loading({ text = 'Loading your workspace…' }: { text?: string }) {
  return <div className="loading-state" role="status"><span className="spinner" />{text}</div>
}


function PageHeading({ title, description, children }: { eyebrow?: string; title: string; description?: string; children?: ReactNode }) {
  return <div className="page-heading"><div><h1>{title}</h1>{description && <p>{description}</p>}</div>{children && <div className="heading-actions">{children}</div>}</div>
}

function Metric({ label, value, icon, note, tone = 'blue' }: { label: string; value: number | undefined; icon: string; note: string; tone?: string }) {
  return <div className="metric"><div className="metric-top"><span>{label}</span><span className={`metric-icon ${tone}`}><Icon name={icon} size={18} /></span></div><strong>{value === undefined ? '—' : number(value)}</strong><p>{note}</p></div>
}

function Brand() { return <span className="brand"><span className="brand-mark"><Icon name="compass" size={25} /></span><span>Leads<span className="brand-light">Finder</span></span></span> }

function AccountBanner({ user }: { user: User }) {
  if (![user.quarantineCount, user.quarantineLimit, user.cleanCount, user.cleanLimit].every(Number.isFinite)) return null
  const hoursLeft = user.trialEndsAtUtc ? Math.max(0, Math.ceil((new Date(user.trialEndsAtUtc).getTime() - Date.now()) / 3_600_000)) : null
  const remaining = hoursLeft === null ? '' : hoursLeft > 24 ? `${Math.ceil(hoursLeft / 24)} days` : `${hoursLeft} hours`
  const nearLimit = user.quarantineCount >= user.quarantineLimit * .9 || user.cleanCount >= user.cleanLimit * .9
  if (user.approved && !nearLimit) return null
  return <aside className="access-notice" aria-label="Account access and capacity"><span>{user.approved ? 'Record capacity is almost full.' : `Awaiting approval · ${remaining} of access remaining.`}</span>{!user.approved && <details><summary>Account deadlines</summary><p>Access ends {date(user.trialEndsAtUtc)}. Without approval, your account and private records are deleted {date(user.deletionAtUtc)}.</p></details>}{nearLimit && <span>Quarantine {number(user.quarantineCount)}/{number(user.quarantineLimit)} · Enriched {number(user.cleanCount)}/{number(user.cleanLimit)}</span>}</aside>
}

function Auth({ onSuccess, expired, accessMessage }: { onSuccess: (user: User) => void; expired: boolean; accessMessage: string | null }) {
  const [register, setRegister] = useState(window.location.pathname === '/register')
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  async function submit(event: FormEvent) {
    event.preventDefault(); setError(null); setBusy(true)
    try { onSuccess(register ? await api.register(name.trim(), email.trim(), password) : await api.login(email.trim(), password)) }
    catch (problem) { setError((problem as Error).message) }
    finally { setBusy(false) }
  }
  return <div className="auth-page"><section className="auth-story"><Brand /><div className="auth-story-content"><h1>LeadsFinder</h1><p>Search businesses, review contacts, and manage sales activity.</p><div className="auth-visual" aria-hidden="true"><div className="auth-orbit orbit-one" /><div className="auth-orbit orbit-two" /><div className="auth-map-grid" /><span className="auth-place place-one"><Icon name="pin" />New Delhi</span><span className="auth-place place-two"><Icon name="pin" />Gurugram</span><span className="auth-place place-three"><Icon name="pin" />Noida</span><div className="auth-result"><span className="metric-icon teal"><Icon name="check" /></span><div><strong>Every detail, connected.</strong><span>Discover → Enrich → Connect</span></div></div><span className="map-dot dot-one" /><span className="map-dot dot-two" /><span className="map-dot dot-three" /></div></div><p className="auth-footer">Demo workspace · Sample data</p></section><main className="auth-form-area"><div className="auth-mobile-brand"><Brand /></div><div className="auth-form-card"><div className="auth-heading-icon"><Icon name={register ? 'spark' : 'compass'} size={27} /></div><span className="eyebrow">YOUR LEAD WORKSPACE</span><h2>{register ? 'Build your pipeline.' : 'Welcome back.'}</h2><p>{register ? 'Create an account to discover and organize your leads.' : 'Sign in to pick up where you left off.'}</p>{expired && !accessMessage && <div className="alert alert-info">Your session has ended. Sign in to continue.</div>}{accessMessage && <div className="alert alert-error auth-access-message" role="alert">{accessMessage}</div>}<form onSubmit={submit}>{register && <label className="field"><span>Full name</span><input autoFocus required autoComplete="name" maxLength={100} placeholder="Your name" value={name} onChange={event => setName(event.target.value)} /></label>}<label className="field"><span>Email address</span><input autoFocus={!register} required type="email" autoComplete="email" maxLength={254} placeholder="you@company.com" value={email} onChange={event => setEmail(event.target.value)} /></label><label className="field"><span>Password</span><input required type="password" autoComplete={register ? 'new-password' : 'current-password'} minLength={register ? 8 : undefined} maxLength={128} placeholder={register ? 'At least 8 characters' : 'Enter your password'} value={password} onChange={event => setPassword(event.target.value)} /></label>{error && <ErrorMessage>{error}</ErrorMessage>}<button className="button button-primary auth-submit" disabled={busy}>{busy ? <><span className="spinner" />{register ? 'Creating account…' : 'Signing in…'}</> : <>{register ? 'Create account' : 'Sign in'}<Icon name="arrow" size={18} /></>}</button></form><p className="auth-toggle">{register ? 'Already have an account?' : 'New to LeadsFinder?'} <button type="button" onClick={() => { setRegister(!register); setError(null) }}>{register ? 'Sign in' : 'Create an account'}</button></p><p className="auth-policy-note"><strong>Account approval</strong><br />Accounts awaiting approval have 3 days of access and are deleted after 10 days. Approval removes both restrictions.</p><div className="auth-note"><Icon name="shield" size={15} /><span>Your records and progress stay connected to your account.</span></div></div></main></div>
}

function JobsTable({ jobs }: { jobs: Job[] }) {
  return <div className="table-scroll"><table className="data-table jobs-table"><thead><tr><th>Search job</th><th>Status</th><th>Records</th><th>Enriched</th><th>Created</th><th><span className="sr-only">Open</span></th></tr></thead><tbody>{jobs.map(job => <tr key={job.id}><td><Link className="table-title" to={`/jobs/${job.id}`}>{job.name || `${placeTypes[job.placeType] ?? 'Lodging'} search`}</Link><span className="table-sub">{placeTypes[job.placeType] ?? job.placeType} · {distance(job.radiusMeters)} radius</span></td><td><Status status={job.status} /></td><td><strong>{number(job.foundCount)}</strong><span className="table-sub">of {job.targetCount} requested</span></td><td><div className="mini-progress"><span style={{ width: `${job.foundCount ? Math.round(job.cleanCount / job.foundCount * 100) : 0}%` }} /></div><span className="table-sub">{job.cleanCount} clean · {job.quarantineCount} pending</span></td><td><span className="table-date">{date(job.createdAtUtc)}</span></td><td><Link className="table-open" to={`/jobs/${job.id}`}><Icon name="arrow" size={19} /><span className="sr-only">Open {job.name}</span></Link></td></tr>)}</tbody></table></div>
}

function DashboardPage() {
  const query = useQuery(api.dashboard, [], 5000)
  const queue = useQuery(() => api.records({ status: 'all', hasContact: true, salesStage: 'uncontacted', dnd: 'exclude', pageSize: 8 }), [], 5000)
  const data = query.data
  return <><PageHeading title="Sales workspace"><Link className="button button-ghost" to="/quarantine">Review records</Link><Link className="button button-primary" to="/discover">New search</Link></PageHeading>
    {query.error && <ErrorMessage retry={query.refresh}>{query.error}</ErrorMessage>}
    <section className="pipeline-totals" aria-label="Sales pipeline">{[
      { label: 'Not contacted', value: data?.uncontactedLeads, to: '/sales?salesStage=uncontacted' },
      { label: 'Contacted', value: data?.contactedLeads, to: '/sales?salesStage=contacted&dnd=all' },
      { label: 'Possible sales', value: data?.possibleSales, to: '/sales?salesStage=possible_sale' },
      { label: 'Do not contact', value: data?.doNotContactLeads, to: '/sales?dnd=only' },
    ].map(item => <Link key={item.label} to={item.to} className="pipeline-total"><span>{item.label}</span><strong>{item.value === undefined ? '—' : number(item.value)}</strong></Link>)}</section>
    <section className="panel"><div className="panel-heading"><h2>Next to contact</h2><Link className="text-link" to="/sales?salesStage=uncontacted">Open lead list →</Link></div>
      {queue.error && <ErrorMessage retry={queue.refresh}>{queue.error}</ErrorMessage>}
      {!queue.data && queue.loading ? <Loading /> : queue.data?.items.length ? <LeadTable leads={queue.data.items} onUpdated={() => { queue.refresh(); query.refresh() }} /> : <Empty title="No uncontacted leads" action={<Link to="/discover" className="button button-primary small">Find leads</Link>}>New contacts will appear here. DND records are excluded.</Empty>}
    </section>
    <section className="panel recent-searches"><div className="panel-heading"><h2>Recent searches{data?.activeJobs ? ` · ${data.activeJobs} running` : ''}</h2><Link className="text-link" to="/jobs">All searches →</Link></div>{data?.recentJobs.length ? <JobsTable jobs={data.recentJobs} /> : <Empty title="No searches yet">Choose an area on the map to start.</Empty>}</section>
  </>
}

function DiscoverPage() {
  const [latitude, setLatitude] = useState(28.6139)
  const [longitude, setLongitude] = useState(77.209)
  const [radius, setRadius] = useState(5000)
  const [placeType, setPlaceType] = useState('any_lodging')
  const [count, setCount] = useState('100')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function submit(event: FormEvent) {
    event.preventDefault(); setError(null)
    if (!Number.isInteger(Number(count)) || Number(count) < 1 || Number(count) > 200) { setError('Choose between 1 and 200 records for one search job.'); return }
    setBusy(true)
    try { const job = await api.createJob({ latitude, longitude, radiusMeters: radius, targetCount: Number(count), placeType }); navigate(`/jobs/${job.id}`) }
    catch (problem) { setError((problem as Error).message) }
    finally { setBusy(false) }
  }
  return <><PageHeading eyebrow="EXPAND YOUR REACH" title="Discover new places" description="Select an area to search the sample catalog." /><div className="discovery-grid"><section className="panel map-panel"><div className="panel-heading"><div><h2>Select your search area</h2><p>Click the map or choose a locality in Delhi NCR.</p></div><span className="region-badge"><Icon name="pin" size={14} />Delhi NCR</span></div><NcrMap latitude={latitude} longitude={longitude} radiusMeters={radius} onCenterChange={(lat, lon) => { setLatitude(lat); setLongitude(lon) }} onRadiusChange={setRadius} /></section><aside className="discovery-settings"><form onSubmit={submit}><section className="panel"><div className="panel-heading"><div><span className="eyebrow">SEARCH SETTINGS</span><h2>Search settings</h2></div><span className="soft-icon"><Icon name="search" /></span></div><div className="panel-body"><label className="field"><span>Place type</span><select value={placeType} onChange={event => setPlaceType(event.target.value)}>{Object.entries(placeTypes).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label className="field"><span>Number of records <span className="field-tag">Max 200</span></span><input required type="number" inputMode="numeric" min={1} max={200} step={1} value={count} onChange={event => setCount(event.target.value)} /><small>Each job can fetch up to 200 new records.</small></label><div className="search-summary"><div><span>Search center</span><strong>{latitude.toFixed(4)}, {longitude.toFixed(4)}</strong></div><div><span>Search radius</span><strong>{distance(radius)}</strong></div><div><span>Requested records</span><strong>{count || '—'}</strong></div></div>{error && <ErrorMessage>{error}</ErrorMessage>}<button className="button button-primary full-width" disabled={busy}>{busy ? <><span className="spinner" />Starting search…</> : <><Icon name="search" size={18} />Find new records<Icon name="arrow" size={18} /></>}</button><p className="form-help">Records you’ve already fetched are automatically skipped.</p></div></section></form></aside></div></>
}

function JobsPage() {
  const [page, setPage] = useState(1)
  const query = useQuery(() => api.jobs(page), [page], 1800)
  return <><PageHeading eyebrow="FROM SEARCH TO COMPLETE" title="Search jobs" description="Track discovery, duplicate checks, and enrichment in one place."><Link className="button button-primary" to="/discover"><Icon name="plus" size={18} />New search</Link></PageHeading>{query.error && <ErrorMessage retry={query.refresh}>{query.error}</ErrorMessage>}<section className="panel"><div className="panel-heading"><div><h2>All jobs <span className="count-badge">{query.data?.total ?? 0}</span></h2><p>Every search stays connected to the records it finds.</p></div><button className="icon-button" aria-label="Refresh jobs" onClick={query.refresh}><Icon name="refresh" size={18} /></button></div>{query.loading && !query.data ? <Loading text="Loading your jobs…" /> : query.data?.items.length ? <><JobsTable jobs={query.data.items} /><Pagination result={query.data} onChange={setPage} loading={query.loading} /></> : <Empty icon="jobs" title="No search jobs yet" action={<Link className="button button-primary small" to="/discover">Create a search<Icon name="arrow" size={16} /></Link>}>Discover places on the map to create your first search job.</Empty>}</section></>
}



function JobDetail({ jobId }: { jobId: string }) {
  const [page, setPage] = useState(1)
  const [status, setStatus] = useState<RecordFilters['status']>('all')
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const jobQuery = useQuery(() => api.job(jobId), [jobId], 1500)
  const recordsQuery = useQuery(() => api.jobRecords(jobId, { page, pageSize: 20, status }), [jobId, page, status], 1500)
  const job = jobQuery.data
  async function enrich() {
    setBusy(true); setActionError(null)
    try { await api.enrichJob(jobId); jobQuery.refresh(); recordsQuery.refresh() }
    catch (problem) { setActionError((problem as Error).message) }
    finally { setBusy(false) }
  }
  if (!job) return <><Link className="back-link" to="/jobs">← All search jobs</Link>{jobQuery.error ? <ErrorMessage retry={jobQuery.refresh}>{jobQuery.error}</ErrorMessage> : <Loading text="Loading search job…" />}</>
  const progress = job.foundCount ? Math.min(100, Math.round(job.processedCount / job.foundCount * 100)) : 0
  return <><Link className="back-link" to="/jobs">← All search jobs</Link><PageHeading eyebrow="SEARCH JOB" title={job.name || 'Search results'} description={`${placeTypes[job.placeType] ?? job.placeType} · ${distance(job.radiusMeters)} radius · Created ${date(job.createdAtUtc)}`}><Status status={job.status} />{job.quarantineCount > 0 && !isActive(job) && <button className="button button-primary" onClick={() => void enrich()} disabled={busy}><Icon name="spark" size={18} />{busy ? 'Starting…' : 'Run enrichment'}</button>}</PageHeading>{jobQuery.error && <ErrorMessage retry={jobQuery.refresh}>{jobQuery.error}</ErrorMessage>}{actionError && <ErrorMessage>{actionError}</ErrorMessage>}{job.status === 'failed' && <ErrorMessage>{job.lastError || 'This job could not finish. Please try another search.'}</ErrorMessage>}{isActive(job) && <div className="live-job"><div className="live-job-icon"><span className="spinner" /></div><div><strong>{job.status === 'fetching' ? 'Searching the sample catalog' : 'Reviewing sample contact details'}</strong><p>{job.status === 'fetching' ? `${job.foundCount} of ${job.targetCount} requested observations found. Previously fetched businesses are skipped.` : `${job.processedCount} of ${job.foundCount} records processed. Completed records move to your clean table.`}</p>{job.status === 'enriching' && <div className="live-progress"><span style={{ width: `${progress}%` }} /></div>}</div><span className="live-label">Updates automatically</span></div>}<section className="metrics-grid"><Metric label="Records found" value={job.foundCount} icon="rows" note={`${job.targetCount} records requested`} /><Metric label="In quarantine" value={job.quarantineCount} icon="clock" note="Waiting for enrichment" tone="amber" /><Metric label="Enriched records" value={job.cleanCount} icon="shield" note="Sample lookup completed" tone="teal" /><Metric label="Duplicates removed" value={job.duplicatesRemoved} icon="check" note="Only unique places are retained" tone="violet" /></section><section className="panel job-summary-panel"><div><Icon name="pin" size={19} /><span>Search center<strong>{job.latitude.toFixed(5)}, {job.longitude.toFixed(5)}</strong></span></div><div><Icon name="compass" size={19} /><span>Search radius<strong>{distance(job.radiusMeters)}</strong></span></div><div><Icon name="jobs" size={19} /><span>Job ID<strong className="monospace">{job.id}</strong></span></div></section>{!isActive(job) && job.quarantineCount > 0 && <div className="enrichment-callout"><span className="enrichment-callout-icon"><Icon name="spark" size={23} /></span><div><h3>Enrich this search</h3><p>Enrichment simulates a lookup and duplicate review. Sample contacts stay non-dialable.</p></div><button className="button button-primary" onClick={() => void enrich()} disabled={busy}>{busy ? 'Starting…' : `Enrich ${job.quarantineCount} records`}<Icon name="arrow" size={17} /></button></div>}{job.status === 'completed' && <div className="alert alert-success"><Icon name="check" size={19} /><span>{job.cleanCount ? `${job.cleanCount} records enriched and ready. Find them in your clean records and sales leads.` : 'Search complete. No new records were found in the selected area. Try another area or a larger radius.'}</span>{job.cleanCount > 0 && <Link to={`/clean?jobId=${job.id}`} className="text-link">View clean records<Icon name="arrow" size={16} /></Link>}</div>}<section className="panel"><div className="panel-heading"><div><h2>Job records <span className="count-badge">{recordsQuery.data?.total ?? job.foundCount}</span></h2><p>Each place is connected to your account and this search.</p></div><div className="segmented" aria-label="Filter job records">{(['all', 'quarantine', 'clean'] as const).map(value => <button key={value} className={status === value ? 'selected' : ''} onClick={() => { setStatus(value); setPage(1) }}>{value === 'all' ? 'All records' : value === 'clean' ? 'Enriched' : 'Quarantine'}</button>)}</div></div>{recordsQuery.error && <ErrorMessage retry={recordsQuery.refresh}>{recordsQuery.error}</ErrorMessage>}{recordsQuery.loading && !recordsQuery.data ? <Loading text="Loading job records…" /> : recordsQuery.data?.items.length ? <><LeadTable leads={recordsQuery.data.items} /><Pagination result={recordsQuery.data} onChange={setPage} loading={recordsQuery.loading} /></> : <Empty icon="rows" title={isActive(job) ? 'Records are on their way' : 'No records here'}>{isActive(job) ? 'Your results will appear here as the job progresses.' : 'Try a different record status or start a new search.'}</Empty>}</section></>
}

async function allJobs() {
  const jobs: Job[] = []
  let page = 1
  while (true) {
    const result = await api.jobs(page, 100)
    jobs.push(...result.items)
    if (!result.items.length || jobs.length >= result.total) return jobs
    page++
  }
}

function AddRecord({ onClose, onAdded }: { onClose: () => void; onAdded: (lead: Lead) => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [name, setName] = useState('')
  const [address, setAddress] = useState('')
  const [phone, setPhone] = useState('')
  const [email, setEmail] = useState('')
  const [website, setWebsite] = useState('')
  const [latitude, setLatitude] = useState('28.6139')
  const [longitude, setLongitude] = useState('77.2090')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { const element = dialog.current; element?.showModal(); return () => element?.close() }, [])
  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(null)
    try { const lead = await api.createRecord({ name: name.trim(), address: address.trim() || null, phone: phone.trim() || null, email: email.trim() || null, website: website.trim() || null, latitude: Number(latitude), longitude: Number(longitude) }); onAdded(lead) }
    catch (problem) { setError((problem as Error).message) }
    finally { setBusy(false) }
  }
  return <dialog ref={dialog} className="record-dialog" onCancel={event => { event.preventDefault(); if (!busy) onClose() }} aria-labelledby="add-record-title"><div className="panel-heading"><div><h2 id="add-record-title">Add a place</h2><p>Save the details you have. Enrich the rest later.</p></div><button className="icon-button" type="button" aria-label="Close add place" disabled={busy} onClick={onClose}><Icon name="close" size={18} /></button></div><form onSubmit={submit} className="panel-body"><label className="field"><span>Place name <em>Required</em></span><input autoFocus required maxLength={200} value={name} onChange={event => setName(event.target.value)} placeholder="e.g. The Imperial Residency" /></label><label className="field"><span>Address</span><input maxLength={500} value={address} onChange={event => setAddress(event.target.value)} placeholder="Street, locality, city" /></label><div className="field-row"><label className="field"><span>Latitude</span><input required type="number" step="any" min={27.8} max={29.4} value={latitude} onChange={event => setLatitude(event.target.value)} /></label><label className="field"><span>Longitude</span><input required type="number" step="any" min={76.4} max={78.2} value={longitude} onChange={event => setLongitude(event.target.value)} /></label></div><div className="field-row"><label className="field"><span>Phone number</span><input type="tel" maxLength={50} value={phone} onChange={event => setPhone(event.target.value)} placeholder="+91…" /></label><label className="field"><span>Email address</span><input type="email" maxLength={254} value={email} onChange={event => setEmail(event.target.value)} placeholder="contact@hotel.com" /></label></div><label className="field"><span>Website</span><input maxLength={300} value={website} onChange={event => setWebsite(event.target.value)} placeholder="https://hotel.com" /></label><div className="inline-note"><Icon name="clock" size={17} /><span>This place will be saved in quarantine and connected to a job for enrichment.</span></div>{error && <ErrorMessage>{error}</ErrorMessage>}<div className="dialog-actions"><button className="button button-ghost" type="button" onClick={onClose} disabled={busy}>Cancel</button><button className="button button-primary" disabled={busy}>{busy ? 'Saving…' : 'Add to quarantine'}<Icon name="plus" size={17} /></button></div></form></dialog>
}

function RecordsPage({ mode }: { mode: 'quarantine' | 'clean' | 'sales' }) {
  const isSales = mode === 'sales'
  const params = new URLSearchParams(window.location.search)
  const [jobId, setJobId] = useState(params.get('jobId') ?? '')
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<RecordFilters['status']>('all')
  const [contact, setContact] = useState<RecordFilters['contact']>('any')
  const [salesStage, setSalesStage] = useState(params.get('salesStage') ?? 'all')
  const [dnd, setDnd] = useState<RecordFilters['dnd']>((params.get('dnd') as RecordFilters['dnd']) || 'exclude')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [addOpen, setAddOpen] = useState(false)
  const [exportOpen, setExportOpen] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const jobsQuery = useQuery(allJobs, [], 10000)
  const filters: RecordFilters = { status: isSales ? status : mode, jobId, q: search, contact, hasContact: isSales, salesStage: isSales ? salesStage : 'all', dnd: isSales ? dnd : 'all', page, pageSize }
  const query = useQuery(() => api.records(filters), [mode, status, jobId, search, contact, page, pageSize, salesStage, dnd], 5000)
  useEffect(() => { if (query.data && query.data.page !== page) setPage(query.data.page) }, [query.data])
  useEffect(() => { const timer = window.setTimeout(() => { setSearch(searchInput); setPage(1) }, 300); return () => window.clearTimeout(timer) }, [searchInput])
  const selectedJob = jobsQuery.data?.find(job => job.id === jobId)
  async function enrichSelected() {
    if (!selectedJob) return
    setBusy(true); setActionError(null); setNotice(null)
    try { await api.enrichJob(selectedJob.id); jobsQuery.refresh(); query.refresh(); setNotice('Sample enrichment started. Progress is saved as records are processed.') }
    catch (problem) { setActionError((problem as Error).message) }
    finally { setBusy(false) }
  }
  const titles = { quarantine: 'Quarantine', clean: 'Enriched records', sales: 'Sales leads' }
  function clearFilters() { setSearchInput(''); setSearch(''); setJobId(''); setSalesStage('all'); setDnd('exclude'); setContact('any'); setStatus('all'); setPage(1) }
  return <><PageHeading title={titles[mode]}>
    <button className="button button-ghost" onClick={() => setExportOpen(true)} disabled={!query.data?.total}>Export for Microsoft Sales</button>
    <button className="button button-ghost" onClick={() => setAddOpen(true)}>Add business</button>
    <Link className="button button-primary" to="/discover">Find leads</Link>
  </PageHeading>
    {notice && <div className="alert alert-success" role="status"><span>{notice}</span><button className="icon-button" aria-label="Dismiss message" onClick={() => setNotice(null)}>×</button></div>}
    {actionError && <ErrorMessage>{actionError}</ErrorMessage>}
    <section className="panel"><div className="table-toolbar"><label className="search-field"><Icon name="search" size={18} /><input type="search" aria-label="Search records" placeholder="Search businesses or contacts" value={searchInput} maxLength={200} onChange={event => setSearchInput(event.target.value)} /></label>
      <div className="toolbar-filters"><select aria-label="Filter by job" value={jobId} onChange={event => { setJobId(event.target.value); setPage(1) }}><option value="">All searches</option>{jobId && !jobsQuery.data?.some(job => job.id === jobId) && <option value={jobId}>Selected search</option>}{jobsQuery.data?.map(job => <option key={job.id} value={job.id}>{job.name}</option>)}</select>
      {isSales && <><select aria-label="Filter sales stage" value={salesStage} onChange={event => { setSalesStage(event.target.value); setPage(1) }}><option value="all">All sales stages</option><option value="uncontacted">Not contacted</option><option value="contacted">Contacted</option>{Object.entries(salesStages).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>
        <select aria-label="Filter DND" value={dnd} onChange={event => { setDnd(event.target.value as RecordFilters['dnd']); setPage(1) }}><option value="exclude">Exclude DND</option><option value="all">Include DND</option><option value="only">DND only</option></select>
        <select aria-label="Filter record status" value={status} onChange={event => { setStatus(event.target.value as RecordFilters['status']); setPage(1) }}><option value="all">All records</option><option value="clean">Enriched</option><option value="quarantine">Quarantine</option></select>
        <select aria-label="Filter contact type" value={contact} onChange={event => { setContact(event.target.value as RecordFilters['contact']); setPage(1) }}><option value="any">Phone or email</option><option value="phone">Has phone</option><option value="email">Has email</option></select></>}
      </div></div>
      <div className="list-summary"><span>{number(query.data?.total ?? 0)} records</span><div>{mode === 'quarantine' && selectedJob && selectedJob.quarantineCount > 0 && <button className="button button-primary small" onClick={() => void enrichSelected()} disabled={busy || isActive(selectedJob)}>{busy ? 'Starting…' : isActive(selectedJob) ? 'Job running…' : 'Enrich this search'}</button>}<label>Rows <select aria-label="Rows per page" value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPage(1) }}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label><button className="button button-ghost small" onClick={clearFilters}>Reset filters</button></div></div>
      {query.error && <ErrorMessage retry={query.refresh}>{query.error}</ErrorMessage>}{jobsQuery.error && <ErrorMessage retry={jobsQuery.refresh}>{jobsQuery.error}</ErrorMessage>}
      {query.loading && !query.data ? <Loading text="Loading records…" /> : query.data?.items.length ? <><LeadTable leads={query.data.items} onUpdated={query.refresh} /><Pagination result={query.data} onChange={setPage} loading={query.loading} /></> : <Empty title="No matching records" action={<button className="button button-ghost small" onClick={clearFilters}>Reset filters</button>}>Change your filters or find more businesses.</Empty>}
    </section>{addOpen && <AddRecord onClose={() => setAddOpen(false)} onAdded={lead => { setAddOpen(false); clearFilters(); query.refresh(); jobsQuery.refresh(); setNotice(`${lead.name} added to quarantine.`) }} />}
    {exportOpen && <ExportDialog filters={filters} onClose={() => setExportOpen(false)} />}
  </>
}

export default function App() {
  const [path, setPath] = useState(window.location.pathname)
  const [routeKey, setRouteKey] = useState(window.location.pathname + window.location.search)
  const [user, setUser] = useState<User | null>(null)
  const [checking, setChecking] = useState(true)
  const [sessionError, setSessionError] = useState<string | null>(null)
  const [expired, setExpired] = useState(false)
  const [accessMessage, setAccessMessage] = useState<string | null>(null)
  const [mobileMenu, setMobileMenu] = useState(false)
  const [loggingOut, setLoggingOut] = useState(false)
  useEffect(() => { const change = () => { setPath(window.location.pathname); setRouteKey(window.location.pathname + window.location.search); setMobileMenu(false) }; window.addEventListener('popstate', change); return () => window.removeEventListener('popstate', change) }, [])
  async function checkSession() {
    setChecking(true); setSessionError(null)
    try { setUser(await api.me()) }
    catch (problem) {
      if (problem instanceof ApiError && (problem.code === 'account_locked' || problem.code === 'account_blocked')) { setUser(null); setAccessMessage(problem.message) }
      else if (!(problem instanceof ApiError && problem.status === 401)) setSessionError((problem as Error).message)
    }
    finally { setChecking(false) }
  }
  useEffect(() => {
    void checkSession()
    const expire = () => { setUser(null); setExpired(true); setAccessMessage(null); setSessionError(null) }
    const unavailable = (event: Event) => { setUser(null); setExpired(false); setSessionError(null); setAccessMessage((event as CustomEvent<{ message: string }>).detail.message) }
    window.addEventListener('session-expired', expire); window.addEventListener('account-unavailable', unavailable)
    return () => { window.removeEventListener('session-expired', expire); window.removeEventListener('account-unavailable', unavailable) }
  }, [])
  useEffect(() => {
    if (!user?.id) return
    let active = true
    let timer: number | undefined
    async function refreshAccount() {
      try { const account = await api.me(); if (active) { setUser(account); setSessionError(null) } }
      catch (problem) {
        if (!active) return
        if (problem instanceof ApiError && problem.status === 401) { setUser(null); setExpired(true); setAccessMessage(null) }
        else if (problem instanceof ApiError && (problem.code === 'account_locked' || problem.code === 'account_blocked')) { setUser(null); setAccessMessage(problem.message) }
        else setSessionError((problem as Error).message)
      }
      finally { if (active) timer = window.setTimeout(() => void refreshAccount(), 3000) }
    }
    timer = window.setTimeout(() => void refreshAccount(), 3000)
    return () => { active = false; window.clearTimeout(timer) }
  }, [user?.id])
  const routeNames: Record<string, string> = { '/': 'Dashboard', '/discover': 'Discover places', '/jobs': 'Search jobs', '/quarantine': 'Quarantine', '/clean': 'Clean records', '/sales': 'Sales leads', '/admin': 'Administration' }
  const activeTitle = path.startsWith('/jobs/') ? 'Job details' : routeNames[path] ?? 'Workspace'
  useEffect(() => { document.title = `LeadsFinder · ${user ? activeTitle : 'Sign in'}` }, [activeTitle, user])
  async function logout() {
    setLoggingOut(true); setSessionError(null)
    try { await api.logout(); setUser(null); setExpired(false); setAccessMessage(null); navigate('/login') }
    catch (problem) { setSessionError((problem as Error).message) }
    finally { setLoggingOut(false) }
  }
  if (checking) return <div className="session-loading"><Brand /><Loading /></div>
  if (!user && sessionError) return <div className="session-loading"><Brand /><ErrorMessage retry={() => void checkSession()}>{sessionError}</ErrorMessage></div>
  if (!user) return <Auth expired={expired} accessMessage={accessMessage} onSuccess={account => { setUser(account); setExpired(false); setAccessMessage(null); if (path === '/login' || path === '/register') navigate('/') }} />
  const nav = [{ to: '/', name: 'Dashboard', icon: 'grid' }, { to: '/discover', name: 'Discover places', icon: 'compass' }, { to: '/jobs', name: 'Search jobs', icon: 'jobs' }, { to: '/quarantine', name: 'Quarantine', icon: 'clock' }, { to: '/clean', name: 'Clean records', icon: 'shield' }]
  const jobMatch = /^\/jobs\/([^/]+)\/?$/.exec(path)
  let page: ReactNode
  if (path === '/' || path === '/login' || path === '/register') page = <DashboardPage />
  else if (path === '/discover') page = <DiscoverPage />
  else if (path === '/jobs') page = <JobsPage />
  else if (jobMatch) page = <JobDetail key={jobMatch[1]} jobId={jobMatch[1]} />
  else if (path === '/quarantine' || path === '/clean' || path === '/sales') page = <RecordsPage key={routeKey} mode={path.slice(1) as 'quarantine' | 'clean' | 'sales'} />
  else if (path === '/admin') page = user.role === 'admin' ? <AdminPage currentUser={user} onSelfUpdated={setUser} /> : <Empty icon="shield" title="Administrator access required" action={<Link to="/" className="button button-primary">Back to dashboard</Link>}>Only administrators can manage accounts and the shared data source.</Empty>
  else page = <Empty title="This page couldn’t be found" action={<Link to="/" className="button button-primary">Back to dashboard</Link>}>Your records are safe. Return to your dashboard to continue.</Empty>
  return <div className="app-shell">{mobileMenu && <button className="sidebar-backdrop" aria-label="Close navigation" onClick={() => setMobileMenu(false)} />}<aside className={`sidebar ${mobileMenu ? 'sidebar-open' : ''}`}><Link to="/" className="brand-link"><Brand /></Link><div className="sidebar-label">WORKSPACE</div><nav aria-label="Main navigation">{nav.map(item => <Link key={item.to} to={item.to} className={`nav-item ${path === item.to || item.to === '/jobs' && !!jobMatch || item.to === '/' && ['/login', '/register'].includes(path) ? 'active' : ''}`}><Icon name={item.icon} size={19} /><span>{item.name}</span>{item.to === '/discover' && <span className="nav-new">+</span>}</Link>)}</nav><div className="sidebar-label sales-nav-label">SALES</div><nav aria-label="Sales navigation"><Link to="/sales" className={`nav-item ${path === '/sales' ? 'active' : ''}`}><Icon name="chart" size={19} /><span>Sales leads</span></Link></nav>{user.role === 'admin' && <><div className="sidebar-label admin-sidebar-section">ADMINISTRATION</div><nav aria-label="Administration navigation"><Link to="/admin" className={`nav-item ${path === '/admin' ? 'active' : ''}`}><Icon name="shield" size={19} /><span>Administration</span></Link></nav></>}<div className="sidebar-region"><span className="region-icon"><Icon name="pin" size={19} /></span><div><strong>Delhi NCR</strong><span>Search region</span></div><span className="region-live" /></div><div className="sidebar-account"><div className="avatar">{user.name.slice(0, 1).toUpperCase()}</div><div className="account-copy"><strong>{user.name}</strong><span>{user.email}</span></div><button type="button" className="logout-button" title="Sign out" aria-label="Sign out" disabled={loggingOut} onClick={() => void logout()}><Icon name="logout" size={18} /></button></div></aside><div className="main-content"><header className="topbar"><div className="topbar-left"><button className="mobile-menu-button icon-button" aria-label="Open navigation" onClick={() => setMobileMenu(true)}><Icon name="menu" size={20} /></button><span className="breadcrumbs">Workspace<span>/</span><strong>{activeTitle}</strong></span></div><span className="demo-indicator">Demo workspace · Sample data</span></header><main className="content-wrap"><AccountBanner user={user} />{sessionError && <ErrorMessage>{sessionError}</ErrorMessage>}{page}<footer><span>LeadsFinder</span><span>Sample catalog · Sales activity is saved</span></footer></main></div></div>
}
