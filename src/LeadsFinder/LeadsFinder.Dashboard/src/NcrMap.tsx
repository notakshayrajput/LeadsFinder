import { useEffect, useId, useRef, useState } from 'react'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import './ncr-map.css'

export type NcrMapProps = {
  latitude: number; longitude: number; radiusMeters: number
  onCenterChange: (latitude: number, longitude: number) => void
  onRadiusChange: (radiusMeters: number) => void
}
const localities: [string, number, number][] = [
  ['New Delhi', 28.6139, 77.209], ['Connaught Place', 28.6315, 77.2167], ['Karol Bagh', 28.6514, 77.1907],
  ['Gurugram', 28.4595, 77.0266], ['Cyber City', 28.4949, 77.0887], ['Noida', 28.5355, 77.391],
  ['Sector 18, Noida', 28.5707, 77.3219], ['Greater Noida', 28.4744, 77.504], ['Ghaziabad', 28.6692, 77.4538],
  ['Faridabad', 28.4089, 77.3178], ['Dwarka', 28.5921, 77.046], ['Rohini', 28.7383, 77.0822],
  ['IGI Airport', 28.5562, 77.1], ['Indirapuram', 28.6415, 77.3702], ['Manesar', 28.3543, 76.9382],
]

export default function NcrMap(props: NcrMapProps) {
  const radiusId = useId()
  const container = useRef<HTMLDivElement>(null)
  const map = useRef<L.Map | null>(null)
  const circle = useRef<L.Circle | null>(null)
  const pin = useRef<L.CircleMarker | null>(null)
  const current = useRef(props)
  current.current = props
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const [tileError, setTileError] = useState(false)
  const [retry, setRetry] = useState(0)
  const matches = localities.filter(([name]) => name.toLowerCase().includes(query.toLowerCase()))
  useEffect(() => {
    if (!container.current) return
    const { latitude, longitude, radiusMeters } = current.current
    const instance = L.map(container.current, { scrollWheelZoom: false, minZoom: 9, maxZoom: 19 })
      .setView([latitude, longitude], 13)
    map.current = instance
    const tiles = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(instance)
    tiles.on('tileerror', () => setTileError(true))
    circle.current = L.circle([latitude, longitude], { radius: radiusMeters, color: '#2563a6', fillOpacity: .08, weight: 2 }).addTo(instance)
    pin.current = L.circleMarker([latitude, longitude], { radius: 7, color: '#fff', fillColor: '#2563a6', fillOpacity: 1, weight: 3 }).addTo(instance)
    instance.on('click', (event: L.LeafletMouseEvent) => {
      const { lat, lng } = event.latlng
      current.current.onCenterChange(Math.min(29.05, Math.max(28.25, lat)), Math.min(77.75, Math.max(76.75, lng)))
    })
    const observer = new ResizeObserver(() => instance.invalidateSize())
    observer.observe(container.current)
    return () => { observer.disconnect(); instance.remove(); map.current = null; circle.current = null; pin.current = null }
  }, [retry])
  useEffect(() => {
    const center: L.LatLngTuple = [props.latitude, props.longitude]
    circle.current?.setLatLng(center).setRadius(props.radiusMeters)
    pin.current?.setLatLng(center)
    if (map.current && !map.current.getBounds().pad(-.15).contains(center)) map.current.panTo(center)
  }, [props.latitude, props.longitude, props.radiusMeters])

  function select(location: [string, number, number]) {
    setQuery(location[0]); setOpen(false)
    props.onCenterChange(location[1], location[2])
    map.current?.setView([location[1], location[2]], Math.max(13, map.current.getZoom()))
  }
  return <div className="ncr-map street-map">
    <div className="ncr-toolbar"><div className="map-locality-search"><input aria-label="Find NCR locality" placeholder="Find a locality…" value={query} onFocus={() => setOpen(true)} onBlur={() => setOpen(false)} onKeyDown={event => { if (event.key === 'Enter' && matches[0]) { event.preventDefault(); select(matches[0]) } if (event.key === 'Escape') setOpen(false) }} onChange={event => { setQuery(event.target.value); setOpen(true) }} />
      {open && <div className="map-locality-results">{matches.slice(0, 8).map(location => <button key={location[0]} type="button" onMouseDown={event => event.preventDefault()} onClick={() => select(location)}>{location[0]}</button>)}{!matches.length && <span>No matching locality</span>}</div>}
    </div><button type="button" className="button button-ghost small" onClick={() => map.current?.fitBounds(circle.current!.getBounds(), { padding: [25, 25], maxZoom: 15 })}>Fit search area</button></div>
    {tileError && <div className="map-error" role="alert">Street tiles could not load. Check your connection.<button type="button" onClick={() => { setTileError(false); setRetry(value => value + 1) }}>Retry</button></div>}
    <div ref={container} className="street-map-canvas" role="region" aria-label="Delhi NCR street map. Use plus and minus to zoom; click to select search center." />
    <div className="map-radius"><label htmlFor={radiusId}>Radius: <strong>{(props.radiusMeters / 1000).toLocaleString('en-IN')} km</strong></label><input id={radiusId} aria-label="Search radius" type="range" min={500} max={50000} step={100} value={props.radiusMeters} onChange={event => props.onRadiusChange(Number(event.target.value))} /><span>{props.latitude.toFixed(4)}, {props.longitude.toFixed(4)}</span></div>
  </div>
}
