import { useEffect, useState } from 'react'
import { api, type DuplicateReport, type Job, type MatchingConfig, type MatchPlace } from './api'

const labels = {
  likely_duplicate: 'Likely duplicate',
  possible_duplicate: 'Needs review',
  likely_distinct: 'Likely different places',
}

function PlaceDetails({ place }: { place: MatchPlace }) {
  return <div className="match-place">
    <a href={place.sourceUrl} target="_blank" rel="noopener noreferrer"><strong>{place.name} ↗</strong></a>
    <p>{place.address || 'Address missing'}</p>
    <p>{place.phone || 'Phone missing'} · {place.website || 'Website missing'}</p>
    <small>{place.strategy} · {place.latitude.toFixed(5)}, {place.longitude.toFixed(5)}</small>
  </div>
}

export default function DuplicateChecks({ job }: { job: Job }) {
  const [config, setConfig] = useState<MatchingConfig | null>(null)
  const [report, setReport] = useState<DuplicateReport | null>(null)
  const [useAi, setUseAi] = useState(false)
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [visibleCount, setVisibleCount] = useState(20)

  useEffect(() => {
    let active = true
    void Promise.all([api.matchingConfig(), api.latestDuplicateCheck(job.id)])
      .then(([settings, result]) => { if (active) { setConfig(settings); setReport(result.report) } })
      .catch((failure: Error) => { if (active) setError(failure.message) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [job.id])

  async function check() {
    setBusy(true)
    setError(null)
    try {
      setReport(await api.checkDuplicates(job.id, useAi))
      setVisibleCount(20)
    } catch (failure) { setError((failure as Error).message) }
    finally { setBusy(false) }
  }

  return <section className="panel detail-panel" id="duplicates" aria-label="Duplicate checks">
    <div className="panel-heading"><div><span className="section-kicker">04 / COMPARE</span>
      <h2>Duplicate checks</h2><p>Compare this job’s places with all saved observations.</p></div></div>
    <div className="match-controls">
      <p>Names and addresses are compared alongside distance. Very similar names within 40 m suggest a duplicate. Each pair stays separate for inspection.</p>
      <label><input type="checkbox" checked={useAi} disabled={!config?.bedrockReady || busy}
        onChange={event => setUseAi(event.target.checked)} /> Ask AWS about potential duplicates
        {config?.bedrockReady ? ` (up to ${config.maxAiCallsPerCheck} comparisons)` : ' · AWS is not configured'}</label>
      <button type="button" className="heading-action" disabled={busy || loading || job.status === 'pending' || job.status === 'running'}
        onClick={() => void check()}>{busy ? 'Comparing places…' : 'Check duplicates'}</button>
      {(job.status === 'pending' || job.status === 'running') && <p>Checks are available when fetching finishes.</p>}
      {error && <div className="alert error" role="alert">{error}</div>}
    </div>
    {loading && <p className="records-empty">Loading previous check…</p>}
    {!loading && !report && <p className="records-empty">Run a check to see potential duplicate pairs.</p>}
    {report && <>
      <div className="match-summary"><strong>{report.pairs.filter(x => x.decision !== 'likely_distinct').length} pairs to inspect</strong>
        <span>{report.scannedCount} observations checked · {report.comparedPairCount} pairs compared · {report.aiCalls} AWS requests</span>
        <small>Checked {new Date(report.createdAtUtc).toLocaleString()}. Text similarity scores measure overlap, not the probability of a match.</small></div>
      {report.truncated && <div className="alert error">This check reached its batch or candidate limit. The report covers only part of the available observations.</div>}
      {report.aiSkipped > 0 && <p className="records-empty">{report.aiSkipped} pairs have local comparisons only because the AWS limit was reached or a request failed.</p>}
      {report.pairs.length === 0 && <p className="records-empty">No potential duplicates were found among the compared candidates.</p>}
      {report.pairs.slice(0, visibleCount).map(pair => <article className="match-card" key={`${pair.left.id}:${pair.right.id}`}>
        <div className="match-card-heading"><strong className={`match-label ${pair.decision}`}>{labels[pair.decision]}</strong>
          <span>{pair.distanceMeters.toFixed(1)} m apart · name {(pair.nameSimilarity * 100).toFixed(0)}% · address {pair.addressSimilarity === null ? 'missing' : `${(pair.addressSimilarity * 100).toFixed(0)}%`}</span></div>
        <div className="match-places"><PlaceDetails place={pair.left} /><PlaceDetails place={pair.right} /></div>
        <ul>{pair.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>
        {pair.ai && <div className="match-ai"><strong>AWS suggestion: {pair.ai.error ? 'unavailable' : pair.ai.decision}</strong>
          <p>{pair.ai.reason}</p>{pair.ai.error && <p>{pair.ai.error}. The local comparison is retained.</p>}
          {pair.ai.evidence.length > 0 && <details><summary>Supporting fields</summary><ul>{pair.ai.evidence.map((item, index) =>
            <li key={index}><strong>{item.field}:</strong> “{item.leftValue}” / “{item.rightValue}”</li>)}</ul></details>}
        </div>}
      </article>)}
      {report.pairs.length > visibleCount && <div className="pagination"><span>Showing {visibleCount} of {report.pairs.length} pairs</span>
        <button type="button" onClick={() => setVisibleCount(count => count + 20)}>Show more</button></div>}
    </>}
  </section>
}
