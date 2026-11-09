// HTTP integration checks against an isolated application/data directory. No external services.
// Build the API first, then run: node scripts/test-demo.mjs
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdir, mkdtemp, readdir, readFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const published = process.argv.includes('--published')
const configuration = process.argv.includes('--release') ? 'Release' : 'Debug'
const project = published ? path.join(root, 'artifacts/publish') : path.join(root, 'src/LeadsFinder/LeadsFinder.Api')
const dll = published ? path.join(project, 'LeadsFinder.Api.dll') : path.join(project, `bin/${configuration}/net10.0/LeadsFinder.Api.dll`)
const artifactRoot = path.join(root, 'artifacts')
await mkdir(artifactRoot, { recursive: true })
const data = await mkdtemp(path.join(artifactRoot, 'workflow-test-'))
const portProbe = createServer()
portProbe.listen(0, '127.0.0.1')
await once(portProbe, 'listening')
const port = portProbe.address().port
await new Promise(resolve => portProbe.close(resolve))
const base = `http://127.0.0.1:${port}`
let server
let log = ''
const pause = ms => new Promise(resolve => setTimeout(resolve, ms))

async function start(catalogSize = 1_000_000) {
  server = spawn('dotnet', [dll, '--urls', base, '--Demo:DataPath', data, '--Demo:CatalogSize', String(catalogSize)], {
    cwd: project,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Demo__TickMilliseconds: '10' },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  })
  server.stdout.on('data', chunk => { log += chunk })
  server.stderr.on('data', chunk => { log += chunk })
  for (let i = 0; i < 150; i++) {
    if (server.exitCode !== null) throw new Error(`API exited early: ${log}`)
    try { if ((await fetch(`${base}/api/auth/me`)).status === 401) return } catch {}
    await pause(100)
  }
  throw new Error(`API did not start: ${log}`)
}

async function stop() {
  if (server && server.exitCode === null && server.signalCode === null) {
    const exited = once(server, 'exit')
    server.kill()
    await exited
  }
  server = undefined
}

function client() {
  let cookie = ''
  return async (route, method = 'GET', body, expected = 200, extraHeaders = {}) => {
    const response = await fetch(`${base}${route}`, {
      method,
      headers: { 'Content-Type': 'application/json', ...(cookie ? { Cookie: cookie } : {}), ...extraHeaders },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    })
    const newCookie = response.headers.get('set-cookie')
    if (newCookie) cookie = newCookie.split(';')[0]
    const raw = await response.text()
    assert.equal(response.status, expected, `${method} ${route}: ${raw}`)
    return raw ? JSON.parse(raw) : null
  }
}

async function waitJob(api, id, finalStatus) {
  for (let i = 0; i < 300; i++) {
    const job = await api(`/api/jobs/${id}`)
    assert.notEqual(job.status, 'failed', job.lastError || 'Job failed')
    if (job.status === finalStatus) return job
    await pause(100)
  }
  throw new Error(`Job ${id} did not reach ${finalStatus}`)
}

const query = { latitude: 28.6139, longitude: 77.209, radiusMeters: 5000, targetCount: 200, placeType: 'any_lodging' }
const placeKey = row => `${row.name}|${row.latitude}|${row.longitude}`
const allRows = async (api, url) => {
  const first = await api(`${url}${url.includes('?') ? '&' : '?'}pageSize=100`)
  const items = [...first.items]
  for (let p = 2; items.length < first.total; p++) {
    const next = await api(`${url}${url.includes('?') ? '&' : '?'}pageSize=100&page=${p}`)
    assert.ok(next.items.length, 'Pagination must advance')
    items.push(...next.items)
  }
  return items
}

try {
  await start()
  if (published) {
    for (const route of ['/', '/quarantine', '/jobs/00000000-0000-0000-0000-000000000000', '/clean', '/sales']) {
      const response = await fetch(`${base}${route}`)
      assert.equal(response.status, 200, `Published SPA route ${route}`)
      assert.match(await response.text(), /<div id="root"><\/div>/)
    }
    assert.equal((await fetch(`${base}/api/unknown-route`)).status, 404)
  }
  const a = client(), b = client(), anonymous = client()
  const password = 'Workflow-test!2026'
  await anonymous('/api/dashboard', 'GET', undefined, 401)
  await anonymous('/api/records', 'GET', undefined, 401)
  await a('/api/auth/register', 'POST', { name: 'First User', email: 'first@example.test', password })
  await b('/api/auth/register', 'POST', { name: 'Second User', email: 'second@example.test', password })
  await anonymous('/api/auth/login', 'POST', { email: 'first@example.test', password: 'incorrect' }, 401)
  await a('/api/jobs', 'POST', { ...query, targetCount: 201 }, 400)
  await a('/api/jobs', 'POST', { ...query, targetCount: 0 }, 400)
  await a('/api/jobs', 'POST', { ...query, latitude: 91 }, 400)
  const firstJob = await a('/api/jobs', 'POST', query, 201)
  const staged = await waitJob(a, firstJob.id, 'quarantined')
  assert.equal(staged.foundCount, 200)
  const originals = await allRows(a, `/api/jobs/${firstJob.id}/records`)
  assert.equal(originals.length, 200)
  assert.ok(originals.some(row => row.missingFields.length > 0))
  assert.ok(originals.every(row => row.status === 'quarantine'))
  await a('/api/records', 'POST', { name: originals[0].name, latitude: originals[0].latitude, longitude: originals[0].longitude }, 409)
  for (const row of originals) {
    const distance = Math.hypot((row.latitude - query.latitude) * 111_320, (row.longitude - query.longitude) * 97_700)
    assert.ok(distance <= 5100, `Out-of-radius row: ${distance}`)
  }
  await b(`/api/jobs/${firstJob.id}`, 'GET', undefined, 404)
  await b(`/api/jobs/${firstJob.id}/records`, 'GET', undefined, 404)
  await b(`/api/jobs/${firstJob.id}/enrich`, 'POST', {}, 404)
  assert.equal((await b('/api/dashboard')).totalRecords, 0)
  assert.equal((await b('/api/records')).total, 0)
  await a('/api/jobs', 'POST', query, 403, { Origin: 'https://another-origin.example' })
  const secondUserJob = await b('/api/jobs', 'POST', query, 201)
  await waitJob(b, secondUserJob.id, 'quarantined')
  const otherUserRows = await allRows(b, `/api/jobs/${secondUserJob.id}/records`)
  assert.deepEqual(otherUserRows.map(placeKey).sort(), originals.map(placeKey).sort(), 'Shared catalog must be reusable across users')
  const nextJob = await a('/api/jobs', 'POST', query, 201)
  await waitJob(a, nextJob.id, 'quarantined')
  const nextRows = await allRows(a, `/api/jobs/${nextJob.id}/records`)
  const seen = new Set(originals.map(placeKey))
  assert.ok(nextRows.every(row => !seen.has(placeKey(row))), 'Repeat search must return unseen businesses')
  await a(`/api/jobs/${firstJob.id}/enrich`, 'POST', {})
  const cleanJob = await waitJob(a, firstJob.id, 'completed')
  assert.ok(cleanJob.duplicatesRemoved > 0, 'Catalog should contain duplicates')
  assert.equal(cleanJob.cleanCount + cleanJob.duplicatesRemoved, cleanJob.foundCount)
  const clean = await allRows(a, `/api/records?status=clean&jobId=${firstJob.id}`)
  assert.equal(clean.length, cleanJob.cleanCount)
  assert.equal(new Set(clean.map(placeKey)).size, clean.length)
  assert.ok(clean.every(row => row.status === 'clean' && row.missingFields.length === 0 && row.address && row.phone && row.email && row.website))
  assert.equal((await a(`/api/records?status=quarantine&jobId=${firstJob.id}`)).total, 0)
  const stats = await a('/api/dashboard')
  assert.equal(stats.enrichedRecords, clean.length)
  assert.equal(stats.totalRecords, stats.enrichedRecords + stats.quarantineRecords)
  const sales = await a('/api/records?status=clean&hasContact=true')
  assert.ok(sales.items.every(row => row.status === 'clean' && (row.phone || row.email)))
  assert.equal(sales.total, clean.length)
  const reviewed = clean[0]
  const savedActivity = await a(`/api/records/${reviewed.id}/sales`, 'PATCH', { stage: 'possible_sale', contactedOnEmail: true, doNotContact: true, notes: 'Needs a quote' })
  assert.equal(savedActivity.sales.stage, 'possible_sale')
  assert.equal(savedActivity.sales.contactedOnEmail, true)
  await b(`/api/records/${reviewed.id}/sales`, 'PATCH', { stage: 'won' }, 404)
  const rated = await a(`/api/records/${reviewed.id}/quality`, 'PUT', { field: 'phone', value: reviewed.phone, confidence: 'low', inaccurate: true })
  assert.equal(rated.fieldQuality.phone.inaccurateReports, 1)
  await a(`/api/records/${reviewed.id}/quality`, 'PUT', { field: 'phone', value: 'stale value', inaccurate: true }, 409)
  const repeatVote = await a(`/api/records/${reviewed.id}/quality`, 'PUT', { field: 'phone', value: reviewed.phone, inaccurate: true })
  assert.equal(repeatVote.fieldQuality.phone.inaccurateReports, 1)
  assert.ok((await a('/api/records?salesStage=possible_sale&dnd=only')).items.some(row => row.id === reviewed.id))
  const lastPage = await a('/api/records?status=clean&page=999999&pageSize=20')
  assert.equal(lastPage.page, Math.ceil(lastPage.total / lastPage.pageSize))
  assert.ok((await b('/api/records?status=quarantine')).items.every(row => row.status === 'quarantine'), 'Other user retains quarantine status')
  const manual = await a('/api/records', 'POST', { name: 'Workflow Hotel', address: null, latitude: 28.62, longitude: 77.21, phone: null, email: null, website: null }, 201)
  assert.equal(manual.status, 'quarantine')
  await a(`/api/jobs/${manual.jobId}/enrich`, 'POST', {})
  await waitJob(a, manual.jobId, 'completed')
  assert.equal((await a(`/api/records?status=clean&jobId=${manual.jobId}`)).total, 1)
  await a('/api/auth/logout', 'POST', {})
  await a('/api/auth/me', 'GET', undefined, 401)
  await a('/api/auth/login', 'POST', { email: 'FIRST@example.test', password })
  const beforeRestart = await a('/api/dashboard')
  await stop()
  const jsonFiles = (await readdir(data)).filter(name => name.endsWith('.json'))
  assert.ok(jsonFiles.length > 0, 'Fetched records must persist to disk')
  for (const name of jsonFiles) assert.ok(!(await readFile(path.join(data, name), 'utf8')).includes(password), 'Passwords must be hashed')
  const db = JSON.parse(await readFile(path.join(data, 'database.json'), 'utf8'))
  const canonicalPlaces = new Set(db.records.map(row => row.businessKey))
  assert.equal(Object.keys(db.businesses).length, canonicalPlaces.size, 'Users must share globally stored businesses')
  await start(10_000_000)
  const persistedStats = await a('/api/dashboard')
  assert.equal(persistedStats.totalRecords, beforeRestart.totalRecords, 'User data and cookie keys survive restart')
  assert.equal(persistedStats.catalogSize, 10_000_000)
  const largeCatalogJob = await a('/api/jobs', 'POST', { ...query, targetCount: 25 }, 201)
  await waitJob(a, largeCatalogJob.id, 'quarantined')
  assert.equal((await a(`/api/jobs/${largeCatalogJob.id}/records`)).total, 25)
  const parallel = await Promise.all([a('/api/jobs', 'POST', { ...query, targetCount: 25 }, 201), a('/api/jobs', 'POST', { ...query, targetCount: 25 }, 201)])
  await Promise.all(parallel.map(job => waitJob(a, job.id, 'quarantined')))
  const [parallelLeft, parallelRight] = await Promise.all(parallel.map(job => allRows(a, `/api/jobs/${job.id}/records`)))
  const parallelKeys = new Set(parallelLeft.map(placeKey))
  assert.ok(parallelRight.every(row => !parallelKeys.has(placeKey(row))), 'Concurrent searches must not repeat businesses across jobs')
  const resumable = await a('/api/jobs', 'POST', query, 201)
  await stop()
  await start(10_000_000)
  const resumed = await waitJob(a, resumable.id, 'quarantined')
  assert.equal(resumed.foundCount, 200, 'Interrupted fetch must resume after restart')
  const emptyJob = await a('/api/jobs', 'POST', { ...query, latitude: 0, longitude: 0 }, 201)
  const empty = await waitJob(a, emptyJob.id, 'completed')
  assert.equal(empty.foundCount, 0)
  console.log(`PASS${published ? ' (published SPA)' : ''}: auth, ownership, 200-row cap, NCR radius, shared catalog, unseen/concurrent history, duplicate cleanup, enrichment, filters, sales, manual rows, restart/resume, empty areas and 10m catalog.`)
  console.log(`Isolated test data: ${data}`)
} catch (error) {
  console.error(log)
  throw error
} finally {
  await stop()
}
