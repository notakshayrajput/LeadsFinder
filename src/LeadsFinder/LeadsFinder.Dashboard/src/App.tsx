import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, type Job, type Page } from './api'
import MapSelector from './MapSelector'
import JobDetailsPage from './JobDetailsPage'
import { formatDate, formatDistance, placeTypes, StatusPill } from './jobUi'

function localDateTime(minutesFromNow: number) {
  const date = new Date(Date.now() + minutesFromNow * 60_000)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function Workspace() {
  const [jobs, setJobs] = useState<Page<Job> | null>(null)
  const [jobsPage, setJobsPage] = useState(1)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [scheduleMode, setScheduleMode] = useState<'now' | 'later'>('now')
  const [scheduledLocal, setScheduledLocal] = useState(localDateTime(10))
  const [placeType, setPlaceType] = useState('any_lodging')
  const [source, setSource] = useState('geofabrik-osm')
  const [latitude, setLatitude] = useState(28.6139)
  const [longitude, setLongitude] = useState(77.2090)
  const [radiusMeters, setRadiusMeters] = useState(5000)
  const [targetCount, setTargetCount] = useState('25')
  const [requirePhone, setRequirePhone] = useState(false)
  const [requireEmail, setRequireEmail] = useState(false)
  const [requireAddress, setRequireAddress] = useState(true)
  const [shortagePolicy, setShortagePolicy] = useState('return_fewer')

  const loadJobs = useCallback(async () => {
    try {
      const page = await api.jobs(jobsPage)
      setJobs(page)
      setLoadError(null)
    } catch (error) {
      setLoadError((error as Error).message)
    }
  }, [jobsPage])

  useEffect(() => {
    void loadJobs()
    const timer = window.setInterval(() => void loadJobs(), 5000)
    return () => window.clearInterval(timer)
  }, [loadJobs])

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSubmitError(null)
    if (!requirePhone && !requireEmail && !requireAddress) {
      setSubmitError('Choose at least one desired field.')
      return
    }
    const scheduledDate = scheduleMode === 'now' ? new Date() : new Date(scheduledLocal)
    if (Number.isNaN(scheduledDate.getTime())) {
      setSubmitError('Choose a valid scheduled date and time.')
      return
    }
    setSubmitting(true)
    try {
      const job = await api.createJob({
        source,
        placeType,
        latitude,
        longitude,
        radiusMeters,
        requirePhone,
        requireEmail,
        requireAddress,
        targetCount: Number(targetCount),
        shortagePolicy,
        scheduledAtUtc: scheduledDate.toISOString(),
      })
      window.location.assign(`/jobs/${job.id}`)
    } catch (error) {
      setSubmitError((error as Error).message)
    } finally {
      setSubmitting(false)
    }
  }

  const activeCount = jobs?.items.filter(job => job.status === 'pending' || job.status === 'running').length ?? 0
  const completedCount = jobs?.items.filter(job => job.status === 'completed').length ?? 0
  const visibleRecords = jobs?.items.reduce((sum, job) => sum + job.quarantinedCount, 0) ?? 0

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand"><span className="brand-mark">◈</span><span>Leads<span className="brand-light">Finder</span><small>SEARCH WORKSPACE</small></span></div>
        <div className="sidebar-caption">WORKSPACE</div>
        <a className="nav-item active" href="#overview"><span className="nav-icon">▦</span> Overview</a>
        <a className="nav-item" href="#new-search"><span className="nav-icon">⌕</span> New search</a>
        <a className="nav-item" href="#jobs"><span className="nav-icon">≡</span> Search jobs</a>
        <div className="sidebar-bottom"><span className="demo-indicator" /> Search sources<small>OpenStreetMap · Google Maps Scraper</small></div>
      </aside>

      <main className="main-content" id="overview">
        <header className="topbar"><div className="breadcrumbs">Workspace <span>/</span> Overview</div><div className="topbar-right"><span className="live-dot" /> Auto refresh · 5s</div></header>
        <div className="content-wrap">
          <div className="page-heading"><div><div className="eyebrow">LEAD DISCOVERY</div><h1>Search workspace</h1><p>Queue a lodging search and inspect what reaches quarantine.</p></div><a className="heading-action" href="#new-search">+ New search</a></div>

          <div className="demo-banner"><span className="banner-icon">ⓘ</span><div><strong>Search source</strong><p>OpenStreetMap searches the cached Geofabrik Northern Zone extract. Google Maps Scraper runs a separate CLI process per job and divides larger areas into grid cells. Both sources save raw observations in quarantine. © OpenStreetMap contributors for OSM data.</p></div></div>

          {loadError && <div className="alert error" role="alert">{loadError} <button type="button" onClick={() => void loadJobs()}>Retry</button></div>}

          <section className="stats-grid" aria-label="Job summary">
            <div className="stat-card"><span className="stat-label">TOTAL SEARCHES</span><strong>{jobs?.total ?? '—'}</strong><span className="stat-foot">Jobs in this workspace</span></div>
            <div className="stat-card"><span className="stat-label">ACTIVE ON THIS PAGE</span><strong>{jobs ? activeCount : '—'}</strong><span className="stat-foot">Pending or running</span></div>
            <div className="stat-card"><span className="stat-label">COMPLETED ON THIS PAGE</span><strong>{jobs ? completedCount : '—'}</strong><span className="stat-foot">Fetch stage complete</span></div>
            <div className="stat-card accent"><span className="stat-label">QUARANTINED ON THIS PAGE</span><strong>{jobs ? visibleRecords : '—'}</strong><span className="stat-foot">Raw observations saved</span></div>
          </section>

          <div className="workspace-grid">
            <section className="panel create-panel" id="new-search">
              <div className="panel-heading"><div><span className="section-kicker">01 / CREATE</span><h2>New search</h2><p>Define a search job for the fetch worker.</p></div><span className="panel-symbol">⌕</span></div>
              <form onSubmit={submit}>
                <div className="form-section-title">SEARCH CRITERIA</div>
                <label className="field"><span>Source</span><select value={source} onChange={event => setSource(event.target.value)}><option value="geofabrik-osm">OpenStreetMap · Northern Zone extract</option><option value="google-maps-scraper">Google Maps Scraper</option></select></label>
                <label className="field"><span>Place type</span><select value={placeType} onChange={event => setPlaceType(event.target.value)}>{Object.entries(placeTypes).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select></label>
                <div className="form-section-title location-title">SEARCH AREA <span>Select on map</span></div>
                <MapSelector latitude={latitude} longitude={longitude} radiusMeters={radiusMeters} onCenterChange={(lat, lon) => { setLatitude(lat); setLongitude(lon) }} onRadiusChange={setRadiusMeters} />
                <div className="form-section-title">RESULT PREFERENCES</div>
                <label className="field"><span>Requested row count</span><input required type="number" min="1" max="500" value={targetCount} onChange={event => setTargetCount(event.target.value)} /><small>Stage up to {Number(targetCount) || 0} new observations from this search area.</small></label>
                <fieldset className="checks"><legend>Desired fields</legend><label><input type="checkbox" checked={requireAddress} onChange={event => setRequireAddress(event.target.checked)} /> Address</label><label><input type="checkbox" checked={requirePhone} onChange={event => setRequirePhone(event.target.checked)} /> Phone</label><label><input type="checkbox" checked={requireEmail} onChange={event => setRequireEmail(event.target.checked)} /> Email</label></fieldset><p className="field-note">Desired fields are used during later lead qualification. Google Maps Scraper also attempts website email extraction when Email is selected; source results may still lack these fields.</p>
                <label className="field"><span>If target is not met</span><select value={shortagePolicy} onChange={event => setShortagePolicy(event.target.value)}><option value="return_fewer">Return fewer observations</option><option value="expand_radius">Expand search up to 50 km</option></select></label>
                <div className="form-section-title">SCHEDULE</div>
                <div className="segmented"><button type="button" className={scheduleMode === 'now' ? 'selected' : ''} onClick={() => setScheduleMode('now')}>Run now</button><button type="button" className={scheduleMode === 'later' ? 'selected' : ''} onClick={() => setScheduleMode('later')}>Schedule for later</button></div>
                {scheduleMode === 'later' && <label className="field"><span>Local date & time</span><input required type="datetime-local" value={scheduledLocal} onChange={event => setScheduledLocal(event.target.value)} /></label>}
                {submitError && <div className="form-error" role="alert">{submitError}</div>}
                <button className="submit-button" type="submit" disabled={submitting}>{submitting ? 'Queueing…' : 'Queue search'} <span>→</span></button>
              </form>
            </section>

            <section className="panel jobs-panel" id="jobs">
              <div className="panel-heading"><div><span className="section-kicker">02 / MONITOR</span><h2>Search jobs</h2><p>Open a job to see its status, saved rows, and errors.</p></div><button type="button" className="icon-button" onClick={() => void loadJobs()} title="Refresh jobs" aria-label="Refresh jobs">↻</button></div>
              <div className="job-list">
                {jobs?.items.length === 0 && <div className="empty-state"><span>⌕</span><strong>No searches yet</strong><p>Create a job to start the workflow.</p></div>}
                {!jobs && !loadError && <div className="empty-state">Loading jobs…</div>}
                {jobs?.items.map(job => <a key={job.id} className="job-row" href={`/jobs/${job.id}`}><div className="job-row-top"><span className="job-type">{placeTypes[job.placeType] ?? job.placeType}</span><StatusPill status={job.status} /></div><div className="job-meta">{job.source === 'google-maps-scraper' ? 'Google Maps' : 'OpenStreetMap'} <span>·</span> {job.latitude.toFixed(4)}, {job.longitude.toFixed(4)} <span>·</span> {formatDistance(job.radiusMeters)}</div><div className="job-row-bottom"><span>{formatDate(job.createdAtUtc)}</span><span>{job.quarantinedCount} in quarantine <b>→</b></span></div></a>)}
              </div>
              {jobs && jobs.total > jobs.pageSize && <div className="pagination"><button disabled={jobsPage === 1} onClick={() => setJobsPage(jobsPage - 1)}>← Previous</button><span>Page {jobsPage} of {Math.ceil(jobs.total / jobs.pageSize)}</span><button disabled={jobsPage * jobs.pageSize >= jobs.total} onClick={() => setJobsPage(jobsPage + 1)}>Next →</button></div>}
            </section>
          </div>

          <footer>LeadsFinder · Discovery and comparison workspace <span>OSM data © OpenStreetMap contributors · Extract by Geofabrik</span></footer>
        </div>
      </main>
    </div>
  )
}

export default function App() {
  const match = /^\/jobs\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?$/i.exec(window.location.pathname)
  if (match) return <JobDetailsPage jobId={match[1]} />
  if (window.location.pathname !== '/') return <main className="job-page-content"><h1>Page not found</h1><a className="heading-action" href="/">Back to workspace</a></main>
  return <Workspace />
}
