// End-to-end administrator checks. All accounts/data are isolated under artifacts/.
// Build the API first, then run: node scripts/test-admin.mjs
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const published = process.argv.includes('--published')
const configuration = process.argv.includes('--release') ? 'Release' : 'Debug'
const project = published ? path.join(root, 'artifacts/publish') : path.join(root, 'src/LeadsFinder/LeadsFinder.Api')
const dll = published ? path.join(project, 'LeadsFinder.Api.dll') : path.join(project, `bin/${configuration}/net10.0/LeadsFinder.Api.dll`)
await mkdir(path.join(root, 'artifacts'), { recursive: true })
const data = await mkdtemp(path.join(root, 'artifacts/admin-test-'))
const probe = createServer()
probe.listen(0, '127.0.0.1')
await once(probe, 'listening')
const port = probe.address().port
await new Promise(resolve => probe.close(resolve))
const base = `http://127.0.0.1:${port}`
const password = 'Admin-workflow!2026'
const pause = ms => new Promise(resolve => setTimeout(resolve, ms))
let server, log = ''

async function start() {
  server = spawn('dotnet', [dll, '--urls', base, '--Demo:DataPath', data], {
    cwd: project, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Demo__TickMilliseconds: '10', Demo__AdminEmail: 'admin@example.test', Demo__AdminPassword: password },
  })
  server.stdout.on('data', chunk => { log += chunk })
  server.stderr.on('data', chunk => { log += chunk })
  for (let i = 0; i < 150; i++) {
    if (server.exitCode !== null) throw new Error(`API exited: ${log}`)
    try { if ((await fetch(`${base}/api/auth/me`)).status === 401) return } catch {}
    await pause(100)
  }
  throw new Error(`API did not start: ${log}`)
}
async function stop() {
  if (server && server.exitCode === null && server.signalCode === null) {
    const exited = once(server, 'exit'); server.kill(); await exited
  }
  server = undefined
}
function client() {
  let cookie = ''
  return async (route, method = 'GET', body, status = 200) => {
    const response = await fetch(`${base}${route}`, { method,
      headers: { 'Content-Type': 'application/json', ...(cookie ? { Cookie: cookie } : {}) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    })
    const next = response.headers.get('set-cookie')
    if (next) cookie = next.split(';')[0]
    const raw = await response.text()
    assert.equal(response.status, status, `${method} ${route}: ${raw}`)
    return raw ? JSON.parse(raw) : null
  }
}
async function waitJob(api, id, status) {
  for (let i = 0; i < 300; i++) {
    const job = await api(`/api/jobs/${id}`)
    assert.notEqual(job.status, 'failed', job.lastError || 'Job failed')
    if (job.status === status) return job
    await pause(100)
  }
  throw new Error(`Job ${id} did not reach ${status}`)
}
const limits = (approved, blocked, quarantineLimit = 1000, cleanLimit = 1000) => ({ approved, blocked, quarantineLimit, cleanLimit })
const query = { latitude: 28.6139, longitude: 77.209, radiusMeters: 5000, targetCount: 5, placeType: 'any_lodging' }
const registration = email => ({ name: email.split('@')[0], email, password })

try {
  await start()
  const admin = client(), user = client(), anonymous = client()
  const owner = await admin('/api/auth/login', 'POST', { email: 'admin@example.test', password })
  assert.equal(owner.role, 'admin')
  const account = await user('/api/auth/register', 'POST', registration('member@example.test'))
  assert.equal(account.role, 'user')
  assert.equal(account.approved, false)
  assert.equal(Date.parse(account.trialEndsAtUtc) - Date.parse(account.createdAtUtc), 3 * 86400000)
  assert.equal(Date.parse(account.deletionAtUtc) - Date.parse(account.createdAtUtc), 10 * 86400000)
  await anonymous('/api/admin/users', 'GET', undefined, 401)
  await user('/api/admin/users', 'GET', undefined, 403)
  await user('/api/admin/overview', 'GET', undefined, 403)
  await user(`/api/admin/users/${account.id}`, 'PATCH', limits(true, false), 403)
  await user('/api/admin/catalog/generate', 'POST', { ...query, count: 10 }, 403)
  const memberSearch = await admin('/api/admin/users?q=member&pageSize=1')
  assert.equal(memberSearch.total, 1)
  assert.equal(memberSearch.items[0].id, account.id)
  await admin(`/api/admin/users/${account.id}`, 'PATCH', limits(false, false, 5, 2))
  await user('/api/jobs', 'POST', { ...query, targetCount: 6 }, 409)
  const cappedJob = await user('/api/jobs', 'POST', query, 201)
  await user('/api/jobs', 'POST', { ...query, targetCount: 1 }, 409)
  await waitJob(user, cappedJob.id, 'quarantined')
  await user('/api/records', 'POST', { name: 'Above quota hotel', latitude: 28.62, longitude: 77.21 }, 409)
  await user(`/api/jobs/${cappedJob.id}/enrich`, 'POST', {}, 409)
  assert.equal((await user('/api/records?status=clean')).total, 0)
  await admin(`/api/admin/users/${account.id}`, 'PATCH', limits(false, false, 3, 10))
  assert.equal((await user('/api/records?status=quarantine')).total, 5, 'Lowering limits preserves existing observations')
  await user('/api/jobs', 'POST', { ...query, targetCount: 1 }, 409)
  await user(`/api/jobs/${cappedJob.id}/enrich`, 'POST', {})
  await waitJob(user, cappedJob.id, 'completed')
  await admin(`/api/admin/users/${account.id}`, 'PATCH', limits(false, true, 10, 10))
  assert.equal((await user('/api/dashboard', 'GET', undefined, 403)).code, 'account_blocked')
  await user('/api/jobs', 'POST', query, 403)
  assert.equal((await user('/api/auth/login', 'POST', { email: account.email, password }, 403)).code, 'account_blocked')
  await admin(`/api/admin/users/${account.id}`, 'PATCH', limits(true, false))
  const approved = await user('/api/auth/login', 'POST', { email: account.email, password })
  assert.equal(approved.approved, true)
  assert.equal(approved.deletionAtUtc, null)

  const before = await admin('/api/admin/overview')
  const generation = { latitude: 12.9716, longitude: 77.5946, radiusMeters: 1500, count: 120 }
  const batch = await admin('/api/admin/catalog/generate', 'POST', generation, 201)
  assert.equal(batch.count, 120)
  const after = await admin('/api/admin/overview')
  assert.equal(after.generatedRows, before.generatedRows + 120)
  assert.equal(after.catalogSize, before.catalogSize + 120)
  const localJob = await user('/api/jobs', 'POST', { ...generation, targetCount: 20, placeType: 'hotel' }, 201)
  await waitJob(user, localJob.id, 'quarantined')
  const generated = await user(`/api/jobs/${localJob.id}/records`)
  assert.equal(generated.total, 20)
  assert.ok(generated.items.every(row => row.missingFields.length === 2 && row.placeType === 'hotel'))
  for (const row of generated.items) {
    const distance = Math.hypot((row.latitude - generation.latitude) * 111320, (row.longitude - generation.longitude) * 111320 * Math.cos(generation.latitude * Math.PI / 180))
    assert.ok(distance < 1520, `Generated row outside selected radius: ${distance}`)
  }
  await user(`/api/jobs/${localJob.id}/enrich`, 'POST', {})
  await waitJob(user, localJob.id, 'completed')
  assert.ok((await user(`/api/jobs/${localJob.id}/records`)).items.every(row => row.missingFields.length === 0))
  await admin('/api/admin/catalog/generate', 'POST', { ...generation, count: 0 }, 400)

  const locked = client(), expired = client()
  const lockedAccount = await locked('/api/auth/register', 'POST', registration('locked@example.test'))
  const expiredAccount = await expired('/api/auth/register', 'POST', registration('expired@example.test'))
  const expiredRow = await expired('/api/records', 'POST', { name: 'Retained global hotel', latitude: 28.62, longitude: 77.21 }, 201)
  await stop()
  const dbPath = path.join(data, 'database.json')
  const db = JSON.parse(await readFile(dbPath, 'utf8'))
  const dateAgo = days => new Date(Date.now() - days * 86400000).toISOString()
  db.users.find(u => u.id === lockedAccount.id).createdAtUtc = dateAgo(4)
  db.users.find(u => u.id === expiredAccount.id).createdAtUtc = dateAgo(11)
  db.users.find(u => u.id === account.id).createdAtUtc = dateAgo(30)
  db.users.find(u => u.id === owner.id).createdAtUtc = dateAgo(100)
  const globalKey = db.records.find(row => row.id === expiredRow.id).businessKey
  await writeFile(dbPath, JSON.stringify(db))
  await start()
  await admin('/api/auth/login', 'POST', { email: 'admin@example.test', password })
  assert.equal((await locked('/api/dashboard', 'GET', undefined, 403)).code, 'account_locked')
  assert.equal((await locked('/api/auth/login', 'POST', { email: lockedAccount.email, password }, 403)).code, 'account_locked')
  await user('/api/auth/me')
  await pause(500)
  const remaining = await admin('/api/admin/users')
  assert.ok(!remaining.items.some(u => u.id === expiredAccount.id))
  assert.ok(remaining.items.some(u => u.id === account.id && u.approved))
  assert.ok(remaining.items.some(u => u.id === owner.id && u.role === 'admin'))
  await expired('/api/auth/me', 'GET', undefined, 401)
  await admin(`/api/admin/users/${lockedAccount.id}`, 'PATCH', limits(true, false))
  await locked('/api/auth/login', 'POST', { email: lockedAccount.email, password })
  assert.ok((await admin('/api/admin/catalog/batches')).some(item => item.id === batch.id))
  await stop()
  const retained = JSON.parse(await readFile(dbPath, 'utf8'))
  assert.ok(!retained.users.some(u => u.id === expiredAccount.id))
  assert.ok(!retained.records.some(row => row.userId === expiredAccount.id))
  assert.ok(!retained.jobs.some(job => job.userId === expiredAccount.id))
  assert.ok(retained.businesses[globalKey], 'Account deletion retains globally shared hotel data')
  console.log('PASS: administrator permissions, approval/blocking, quotas/reservations, generation/geography, active-session restrictions, three-day lock, ten-day account cleanup, approved/admin exemptions and persistence.')
  console.log(`Isolated test data: ${data}`)
} catch (error) {
  console.error(log.slice(-10000))
  throw error
} finally {
  await stop()
}
