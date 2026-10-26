import type { Job } from './api'

export const placeTypes: Record<string, string> = {
  any_lodging: 'All lodging',
  hotel: 'Hotels',
  bed_and_breakfast: 'Bed & breakfasts',
  guest_house: 'Guest houses',
  vacation_rental: 'Vacation rentals',
}

export function formatDate(value: string | null) {
  if (!value) return '—'
  return new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export function formatDistance(meters: number) {
  return meters >= 1000 ? `${(meters / 1000).toLocaleString()} km` : `${meters} m`
}

export function sourceLabel(source: string) {
  return source === 'google-maps-scraper' ? 'Google Maps Scraper' : source === 'geofabrik-osm' ? 'OpenStreetMap' : source
}

export function StatusPill({ status }: { status: Job['status'] }) {
  return <span className={`status-pill status-${status}`}><span className="status-dot" />{status.charAt(0).toUpperCase() + status.slice(1)}</span>
}
