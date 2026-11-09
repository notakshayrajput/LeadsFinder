export interface User {
  id: string; name: string; email: string; role: 'admin' | 'user'; approved: boolean; blocked: boolean
  createdAtUtc: string; lastLoginAtUtc: string | null; trialEndsAtUtc: string | null; deletionAtUtc: string | null
  quarantineLimit: number; cleanLimit: number; quarantineCount: number; cleanCount: number
  status: 'active' | 'trial' | 'locked' | 'blocked'
}
export interface AdminUser extends User { totalJobs: number }
export interface GenerationBatch {
  id: string; latitude: number; longitude: number; radiusMeters: number; count: number
  createdAtUtc: string; createdByName: string; locality: string
}
export interface AdminOverview {
  totalUsers: number; approvedUsers: number; blockedUsers: number; lockedUsers: number; totalJobs: number
  globalRecords: number; catalogSize: number; generatedRows: number; generationBatches: GenerationBatch[]
}
export interface UpdateAdminUser { approved: boolean; blocked: boolean; quarantineLimit: number; cleanLimit: number }
export interface Page<T> { items: T[]; total: number; page: number; pageSize: number }
export type JobStatus = 'fetching' | 'quarantined' | 'enriching' | 'completed' | 'failed'
export interface Job {
  id: string; name: string; status: JobStatus; placeType: string
  latitude: number; longitude: number; radiusMeters: number; targetCount: number
  foundCount: number; quarantineCount: number; cleanCount: number
  duplicatesRemoved: number; processedCount: number; createdAtUtc: string
  finishedAtUtc: string | null; lastError: string | null
}
export interface Lead {
  id: string; name: string; address: string | null; latitude: number; longitude: number
  phone: string | null; email: string | null; website: string | null; locality: string
  placeType: string; status: 'quarantine' | 'clean'; jobId: string; jobName: string
  foundAtUtc: string; enrichedAtUtc: string | null; missingFields: string[]
  sales: SalesProfile; fieldQuality: Record<ReviewField, FieldQuality>; isSample: boolean
}
export type SalesStage = 'new' | 'in_progress' | 'possible_sale' | 'won' | 'lost'
export type ReviewField = 'address' | 'phone' | 'email' | 'website'
export interface SalesProfile {
  stage: SalesStage; contactedOnEmail: boolean; contactedOnPhone: boolean; contactedOnWhatsapp: boolean
  doNotContact: boolean; notes: string; updatedAtUtc: string | null
}
export interface FieldQuality { confidence: 'low' | 'medium' | 'high' | null; inaccurateReports: number; reportedByMe: boolean; communitySignal: string }
// Older API processes and pre-upgrade records may omit the added nested fields.
// Normalize at the display boundary so they can still be opened during upgrades.
export function normalizeLead(lead: Lead): Lead {
  const emptyQuality: FieldQuality = { confidence: null, inaccurateReports: 0, reportedByMe: false, communitySignal: 'unrated' }
  const fields = ['address', 'phone', 'email', 'website'] as const
  return {
    ...lead,
    sales: {
      stage: lead.sales?.stage ?? 'new', contactedOnEmail: lead.sales?.contactedOnEmail ?? false,
      contactedOnPhone: lead.sales?.contactedOnPhone ?? false, contactedOnWhatsapp: lead.sales?.contactedOnWhatsapp ?? false,
      doNotContact: lead.sales?.doNotContact ?? false, notes: lead.sales?.notes ?? '', updatedAtUtc: lead.sales?.updatedAtUtc ?? null,
    },
    fieldQuality: Object.fromEntries(fields.map(field => [field, { ...emptyQuality, ...lead.fieldQuality?.[field] }])) as Record<ReviewField, FieldQuality>,
    missingFields: lead.missingFields ?? [],
    isSample: lead.isSample ?? !!(lead.email?.endsWith('.example') || lead.phone?.replace(/\D/g, '').startsWith('9100000')),
  }
}
export interface Dashboard {
  totalRecords: number; enrichedRecords: number; quarantineRecords: number; salesLeads: number
  totalJobs: number; activeJobs: number; catalogSize: number; recentJobs: Job[]
  uncontactedLeads: number; possibleSales: number; contactedLeads: number; doNotContactLeads: number
}
export interface CreateJob {
  latitude: number; longitude: number; radiusMeters: number; targetCount: number; placeType: string
}
export interface CreateLead {
  name: string; address: string | null; latitude: number; longitude: number
  phone: string | null; email: string | null; website: string | null; jobId?: string
}
export interface RecordFilters {
  status?: 'quarantine' | 'clean' | 'all'; jobId?: string; q?: string
  contact?: 'any' | 'phone' | 'email'; hasContact?: boolean; page?: number; pageSize?: number
  salesStage?: string; dnd?: 'all' | 'exclude' | 'only'
}
export class ApiError extends Error {
  constructor(message: string, public readonly status: number, public readonly code?: string) { super(message); this.name = 'ApiError' }
}
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, { ...init, credentials: 'same-origin', headers: { 'Content-Type': 'application/json', ...init?.headers } })
  } catch { throw new Error('Unable to connect. Check your connection and try again.') }
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    const fieldError = problem?.errors && Object.values(problem.errors).flat()[0]
    const message = typeof fieldError === 'string' ? fieldError : problem?.error || problem?.detail || problem?.title || 'Something went wrong. Please try again.'
    const code = typeof problem?.code === 'string' ? problem.code : undefined
    if (response.status === 403 && (code === 'account_locked' || code === 'account_blocked') && !path.endsWith('/auth/login') && !path.endsWith('/auth/register'))
      window.dispatchEvent(new CustomEvent('account-unavailable', { detail: { message, code } }))
    else if (response.status === 401 && !path.includes('/auth/')) window.dispatchEvent(new Event('session-expired'))
    throw new ApiError(message, response.status, code)
  }
  if (response.status === 204) return {} as T
  if (response.headers.get('content-type')?.includes('text/csv')) return response.blob() as Promise<T>
  return response.json() as Promise<T>
}
function filtersQuery(filters: RecordFilters) {
  const params = new URLSearchParams()
  Object.entries(filters).forEach(([key, value]) => { if (value !== undefined && value !== '') params.set(key, String(value)) })
  return params.toString()
}
export const api = {
  me: () => request<User>('/api/auth/me'),
  login: (email: string, password: string) => request<User>('/api/auth/login', { method: 'POST', body: JSON.stringify({ email, password }) }),
  register: (name: string, email: string, password: string) => request<User>('/api/auth/register', { method: 'POST', body: JSON.stringify({ name, email, password }) }),
  logout: () => request<Record<string, never>>('/api/auth/logout', { method: 'POST' }),
  dashboard: () => request<Dashboard>('/api/dashboard'),
  jobs: (page = 1, pageSize = 20) => request<Page<Job>>(`/api/jobs?page=${page}&pageSize=${pageSize}`),
  job: (id: string) => request<Job>(`/api/jobs/${encodeURIComponent(id)}`),
  createJob: (job: CreateJob) => request<Job>('/api/jobs', { method: 'POST', body: JSON.stringify(job) }),
  enrichJob: (id: string) => request<Job>(`/api/jobs/${encodeURIComponent(id)}/enrich`, { method: 'POST' }),
  records: (filters: RecordFilters = {}) => request<Page<Lead>>(`/api/records?${filtersQuery(filters)}`),
  jobRecords: (id: string, filters: RecordFilters = {}) => request<Page<Lead>>(`/api/jobs/${encodeURIComponent(id)}/records?${filtersQuery(filters)}`),
  createRecord: (lead: CreateLead) => request<Lead>('/api/records', { method: 'POST', body: JSON.stringify(lead) }),
  updateSales: (id: string, changes: Partial<SalesProfile>) => request<Lead>(`/api/records/${encodeURIComponent(id)}/sales`, { method: 'PATCH', body: JSON.stringify(changes) }),
  reviewField: (id: string, review: { field: ReviewField; value: string; confidence?: string; inaccurate?: boolean; reason?: string }) => request<Lead>(`/api/records/${encodeURIComponent(id)}/quality`, { method: 'PUT', body: JSON.stringify(review) }),
  exportUrl: (filters: RecordFilters, includeSamples: boolean) => `/api/records/export?${filtersQuery(filters)}&includeSamples=${includeSamples}`,
  exportRecords: (filters: RecordFilters, includeSamples: boolean) => request<Blob>(`/api/records/export?${filtersQuery(filters)}&includeSamples=${includeSamples}`),
  adminOverview: () => request<AdminOverview>('/api/admin/overview'),
  adminUsers: (q = '', page = 1) => request<Page<AdminUser>>(`/api/admin/users?q=${encodeURIComponent(q)}&page=${page}&pageSize=20`),
  updateUser: (id: string, update: UpdateAdminUser) => request<AdminUser>(`/api/admin/users/${encodeURIComponent(id)}`, { method: 'PATCH', body: JSON.stringify(update) }),
  generateCatalog: (batch: { latitude: number; longitude: number; radiusMeters: number; count: number }) => request<GenerationBatch>('/api/admin/catalog/generate', { method: 'POST', body: JSON.stringify(batch) }),
  generationBatches: () => request<GenerationBatch[]>('/api/admin/catalog/batches'),
}
