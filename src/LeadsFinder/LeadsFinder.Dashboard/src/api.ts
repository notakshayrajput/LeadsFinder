export type JobStatus = 'pending' | 'running' | 'completed' | 'failed'

export interface Job {
  id: string
  status: JobStatus
  source: string
  placeType: string
  latitude: number
  longitude: number
  radiusMeters: number
  requirePhone: boolean
  requireEmail: boolean
  requireAddress: boolean
  targetCount: number
  shortagePolicy: string
  createdAtUtc: string
  scheduledAtUtc: string
  startedAtUtc: string | null
  finishedAtUtc: string | null
  attemptCount: number
  lastError: string | null
  quarantinedCount: number
}

export interface SourceRecord {
  id: string
  searchJobId: string
  strategy: string
  sourceId: string
  sourceLink: string
  name: string
  address: string
  latitude: number
  longitude: number
  phone: string | null
  email: string | null
  website: string | null
  fetchedAtUtc: string
  rawJson: string
}

export interface Page<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

export interface LocationSearchResult {
  id: string
  displayName: string
  latitude: number
  longitude: number
}

export interface CreateJob {
  source: string
  placeType: string
  latitude: number
  longitude: number
  radiusMeters: number
  requirePhone: boolean
  requireEmail: boolean
  requireAddress: boolean
  targetCount: number
  shortagePolicy: string
  scheduledAtUtc: string
}

export interface MatchPlace {
  id: string
  name: string
  address: string
  latitude: number
  longitude: number
  phone: string | null
  email: string | null
  website: string | null
  strategy: string
  sourceId: string
  sourceUrl: string
}

export interface DuplicatePair {
  left: MatchPlace
  right: MatchPlace
  distanceMeters: number
  nameSimilarity: number
  addressSimilarity: number | null
  decision: 'likely_duplicate' | 'possible_duplicate' | 'likely_distinct'
  ruleDecision: string
  reasons: string[]
  ai: null | {
    decision: string
    reason: string
    evidence: { field: string; leftValue: string; rightValue: string }[]
    modelId: string | null
    error: string | null
    inputTokens: number
    outputTokens: number
  }
}

export interface DuplicateReport {
  id: string
  searchJobId: string
  createdAtUtc: string
  ruleVersion: string
  scannedCount: number
  comparedPairCount: number
  truncated: boolean
  aiCalls: number
  aiSkipped: number
  pairs: DuplicatePair[]
}

export interface MatchingConfig {
  bedrockReady: boolean
  maxAiCallsPerCheck: number
}

export class ApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message)
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      headers: { 'Content-Type': 'application/json', ...init?.headers },
    })
  } catch {
    throw new Error('Cannot reach the API. Check that the API and database are running.')
  }

  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    const firstFieldError = problem?.errors && Object.values(problem.errors).flat()[0]
    throw new ApiError(typeof firstFieldError === 'string'
      ? firstFieldError
      : problem?.error || problem?.detail || problem?.title || (response.status >= 500
        ? 'The API could not complete the request. Check that the API and database are running.'
        : `Request failed (${response.status}).`), response.status)
  }
  return response.json() as Promise<T>
}

export const api = {
  matchingConfig: () => request<MatchingConfig>('/api/duplicate-matching/config'),
  latestDuplicateCheck: (id: string) => request<{ report: DuplicateReport | null }>(`/api/jobs/${id}/duplicate-checks/latest`),
  checkDuplicates: (id: string, useAi: boolean) => request<DuplicateReport>(`/api/jobs/${id}/duplicate-checks`, {
    method: 'POST', body: JSON.stringify({ useAi }),
  }),
  locations: (query: string, signal?: AbortSignal) => request<LocationSearchResult[]>(
    `/api/locations?q=${encodeURIComponent(query)}`, { signal }),
  jobs: (page = 1) => request<Page<Job>>(`/api/jobs?page=${page}&pageSize=20`),
  job: (id: string, signal?: AbortSignal) => request<Job>(`/api/jobs/${id}`, { signal }),
  records: (id: string, page = 1, signal?: AbortSignal) => request<Page<SourceRecord>>(`/api/jobs/${id}/records?page=${page}&pageSize=100`, { signal }),
  createJob: (job: CreateJob) => request<Job>('/api/jobs', {
    method: 'POST', body: JSON.stringify(job),
  }),
}
