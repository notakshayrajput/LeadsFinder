import { useEffect, useState } from 'react'
import { api, ApiError, type Job, type Page, type SourceRecord } from './api'
import DuplicateChecks from './DuplicateChecks'
import SavedRowsGrid from './SavedRowsGrid'
import { formatDate, formatDistance, placeTypes, sourceLabel, StatusPill } from './jobUi'

export default function JobDetailsPage({ jobId }: { jobId: string }) {
  const [job, setJob] = useState<Job | null>(null)
  const [records, setRecords] = useState<Page<SourceRecord> | null>(null)
  const [page, setPage] = useState(1)
  const [refresh, setRefresh] = useState(0)
  const [jobError, setJobError] = useState<string | null>(null)
  const [recordsError, setRecordsError] = useState<string | null>(null)
  const [notFound, setNotFound] = useState(false)
  const [refreshing, setRefreshing] = useState(false)

  useEffect(() => {
    let active = true
    const controller = new AbortController()
    let timer: number | undefined
    setRecords(null)
    setRecordsError(null)
    const load = async () => {
      setRefreshing(true)
      const [jobResult, recordsResult] = await Promise.allSettled([
        api.job(jobId, controller.signal), api.records(jobId, page, controller.signal),
      ])
      if (!active) return
      let poll = false
      if (jobResult.status === 'fulfilled') {
        setJob(jobResult.value)
        setJobError(null)
        setNotFound(false)
        poll = jobResult.value.status === 'pending' || jobResult.value.status === 'running'
      } else {
        const missing = jobResult.reason instanceof ApiError && jobResult.reason.status === 404
        setNotFound(missing)
        setJobError(missing ? 'This search job could not be found.' : (jobResult.reason as Error).message)
        poll = !missing
      }
      if (recordsResult.status === 'fulfilled') {
        setRecords(recordsResult.value)
        setRecordsError(null)
      } else {
        setRecordsError((recordsResult.reason as Error).message)
      }
      setRefreshing(false)
      if (poll) timer = window.setTimeout(() => void load(), 5000)
    }
    void load()
    return () => { active = false; controller.abort(); window.clearTimeout(timer) }
  }, [jobId, page, refresh])

  useEffect(() => {
    document.title = job ? `LeadsFinder · ${placeTypes[job.placeType] ?? 'Lodging'} search · ${job.status}` : 'LeadsFinder · Search job'
    return () => { document.title = 'LeadsFinder · Search workspace' }
  }, [job])

  const activeJob = job?.status === 'pending' || job?.status === 'running'
  const desiredFields = job ? [job.requireAddress && 'Address', job.requirePhone && 'Phone', job.requireEmail && 'Email'].filter(Boolean).join(', ') : ''

  return <div className="job-page">
    <header className="job-page-topbar"><a className="job-page-brand" href="/">◈ LeadsFinder</a><a href="/#jobs">← Back to search jobs</a></header>
    <main className="job-page-content">
      <div className="page-heading"><div><div className="eyebrow">SEARCH JOB</div>
        <h1>{notFound ? 'Job not found' : job ? `${placeTypes[job.placeType] ?? job.placeType} search` : 'Search details'}</h1>
        <p>{job ? `${sourceLabel(job.source)} · Created ${formatDate(job.createdAtUtc)}` : 'Status, saved observations, and search results.'}</p></div>
        <div className="job-page-actions">{job && <StatusPill status={job.status} />}<button type="button" className="heading-action"
          disabled={refreshing} onClick={() => setRefresh(value => value + 1)}>{refreshing ? 'Refreshing…' : 'Refresh job'}</button></div>
      </div>

      {jobError && <div className="alert error" role="alert">{jobError}</div>}
      {!job && !jobError && <div className="panel records-empty" role="status">Loading search job…</div>}
      {job && <>
        {job.status === 'failed' && <section className="job-failure" role="alert" aria-label="Search failure reason">
          <h2>Search failed</h2><p>{job.lastError || 'The worker did not record a detailed failure reason.'}</p>
          <small>Attempt {job.attemptCount} · Finished {formatDate(job.finishedAtUtc)}</small></section>}
        {activeJob && <div className="alert job-live" role="status"><span className="live-dot" /><span>
          {job.status === 'pending' ? `Waiting for the worker. Scheduled for ${formatDate(job.scheduledAtUtc)}.` : 'Fetching source observations. Saved rows will appear when the worker commits its results.'}
          {' '}This page refreshes every 5 seconds.</span></div>}

        <section className="job-stats" aria-label="Search result summary">
          <div className="stat-card"><span className="stat-label">SAVED ROWS</span><strong>{job.quarantinedCount}</strong><span className="stat-foot">Observations in quarantine</span></div>
          <div className="stat-card"><span className="stat-label">REQUESTED ROWS</span><strong>{job.targetCount}</strong><span className="stat-foot">Up to this many new observations</span></div>
          <div className="stat-card"><span className="stat-label">SEARCH RADIUS</span><strong>{formatDistance(job.radiusMeters)}</strong><span className="stat-foot">{job.shortagePolicy === 'expand_radius' ? 'May expand up to 50 km' : 'Stay within the selected circle'}</span></div>
        </section>

        <section className="panel job-settings" aria-label="Search settings">
          <div className="panel-heading"><div><h2>Search details</h2><p>The settings and timings for this job.</p></div></div>
          <dl className="job-settings-grid">
            <div><dt>Job ID</dt><dd><code>{job.id}</code></dd></div>
            <div><dt>Source</dt><dd>{sourceLabel(job.source)}</dd></div>
            <div><dt>Search center</dt><dd>{job.latitude.toFixed(5)}, {job.longitude.toFixed(5)}</dd></div>
            <div><dt>Desired fields</dt><dd>{desiredFields || 'None specified'}</dd></div>
            <div><dt>Scheduled</dt><dd>{formatDate(job.scheduledAtUtc)}</dd></div>
            <div><dt>Started</dt><dd>{job.startedAtUtc ? formatDate(job.startedAtUtc) : 'Not started yet'}</dd></div>
            <div><dt>Finished</dt><dd>{job.finishedAtUtc ? formatDate(job.finishedAtUtc) : 'Not finished yet'}</dd></div>
            <div><dt>Attempts</dt><dd>{job.attemptCount}</dd></div>
          </dl>
        </section>

        <section className="panel detail-panel" id="quarantine" aria-label="Saved search rows">
          <div className="panel-heading"><div><h2>Saved observations</h2><p>Raw source rows; missing details can be filled during enrichment.</p></div>
            <span className="job-row-count">{records?.total ?? job.quarantinedCount} rows</span></div>
          {recordsError && <div className="alert error" role="alert">Could not load saved rows: {recordsError}</div>}
          {!records && !recordsError && <p className="records-empty" role="status">Loading saved rows…</p>}
          {records?.items.length === 0 && <div className="records-empty">{job.status === 'failed'
            ? 'No rows were saved. See the failure reason above.'
            : activeJob ? 'No rows have been saved yet.'
              : 'No new rows were saved. Source coverage, the search radius, and previously saved source IDs affect the results.'}</div>}
          {records && records.items.length > 0 && <SavedRowsGrid records={records.items} />}
          {records && records.total > records.pageSize && <nav className="pagination" aria-label="Saved rows pages">
            <button type="button" disabled={page === 1 || refreshing} onClick={() => setPage(value => value - 1)}>← Previous</button>
            <span>Page {page} of {Math.ceil(records.total / records.pageSize)}</span>
            <button type="button" disabled={page * records.pageSize >= records.total || refreshing} onClick={() => setPage(value => value + 1)}>Next →</button>
          </nav>}
        </section>
        <DuplicateChecks key={job.id} job={job} />
      </>}
      <footer>LeadsFinder · Search job <span>OSM data © OpenStreetMap contributors · Extract by Geofabrik</span></footer>
    </main>
  </div>
}
