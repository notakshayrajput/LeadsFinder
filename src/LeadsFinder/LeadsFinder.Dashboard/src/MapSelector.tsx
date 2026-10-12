import { useCallback, useEffect, useRef, useState } from 'react'
import * as L from 'leaflet'
import { api, type LocationSearchResult } from './api'
import 'leaflet/dist/leaflet.css'
import './map.css'

interface Props {
  latitude: number
  longitude: number
  radiusMeters: number
  onCenterChange: (latitude: number, longitude: number) => void
  onRadiusChange: (radiusMeters: number) => void
}

interface CurrentLocation {
  latitude: number
  longitude: number
  accuracy: number
}

const presets = [
  { name: 'Delhi', latitude: 28.6139, longitude: 77.2090 },
  { name: 'Jaipur', latitude: 26.9124, longitude: 75.7873 },
  { name: 'Chandigarh', latitude: 30.7333, longitude: 76.7794 },
  { name: 'Lucknow', latitude: 26.8467, longitude: 80.9462 },
  { name: 'Srinagar', latitude: 34.0837, longitude: 74.7973 },
]

const minimumRadiusMeters = 100
const maximumSliderRadiusMeters = 10_000
const maximumTypedRadiusMeters = 50_000

export default function MapSelector({ latitude, longitude, radiusMeters, onCenterChange, onRadiusChange }: Props) {
  const [radiusInput, setRadiusInput] = useState(String(radiusMeters))
  const [placeQuery, setPlaceQuery] = useState('')
  const [placeResults, setPlaceResults] = useState<LocationSearchResult[]>([])
  const [placeMessage, setPlaceMessage] = useState<string | null>(null)
  const [searchingPlace, setSearchingPlace] = useState(false)
  const [myLocation, setMyLocation] = useState<CurrentLocation | null>(null)
  const [locating, setLocating] = useState(false)
  const [locationMessage, setLocationMessage] = useState<string | null>(null)

  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<L.Map | null>(null)
  const markerRef = useRef<L.Marker | null>(null)
  const circleRef = useRef<L.Circle | null>(null)
  const myMarkerRef = useRef<L.CircleMarker | null>(null)
  const accuracyRef = useRef<L.Circle | null>(null)
  const watchIdRef = useRef<number | null>(null)
  const startWatchRef = useRef<(() => void) | null>(null)
  const searchAbortRef = useRef<AbortController | null>(null)
  const userMovedPinRef = useRef(false)
  const centeredOnLocationRef = useRef(false)
  const onCenterChangeRef = useRef(onCenterChange)
  onCenterChangeRef.current = onCenterChange

  const movePin = useCallback((nextLatitude: number, nextLongitude: number, userSelected: boolean) => {
    if (userSelected) userMovedPinRef.current = true
    const center = L.latLng(nextLatitude, nextLongitude)
    markerRef.current?.setLatLng(center)
    circleRef.current?.setLatLng(center)
    onCenterChangeRef.current(nextLatitude, nextLongitude)
    const circle = circleRef.current
    if (circle) mapRef.current?.fitBounds(circle.getBounds(), { padding: [28, 28], maxZoom: 17 })
  }, [])

  const usePosition = useCallback((position: GeolocationPosition, centerPin: boolean) => {
    const { latitude: nextLatitude, longitude: nextLongitude, accuracy } = position.coords
    if (!Number.isFinite(nextLatitude) || !Number.isFinite(nextLongitude)) return
    setMyLocation({ latitude: nextLatitude, longitude: nextLongitude, accuracy })
    setLocationMessage(null)
    if (centerPin) movePin(nextLatitude, nextLongitude, false)
  }, [movePin])

  useEffect(() => {
    if (!containerRef.current) return
    const map = L.map(containerRef.current, { scrollWheelZoom: false, zoomControl: false })
      .setView([latitude, longitude], 11)
    L.control.zoom({ position: 'bottomright' }).addTo(map)
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>',
    }).addTo(map)

    const icon = L.divIcon({ className: 'search-pin', html: '<span></span>', iconSize: [26, 34], iconAnchor: [13, 32] })
    const marker = L.marker([latitude, longitude], { icon, draggable: true }).addTo(map)
    const circle = L.circle([latitude, longitude], {
      radius: radiusMeters,
      color: '#558c4f', weight: 2, fillColor: '#a8dc72', fillOpacity: 0.18,
    }).addTo(map)
    map.on('click', event => {
      userMovedPinRef.current = true
      marker.setLatLng(event.latlng)
      circle.setLatLng(event.latlng)
      onCenterChangeRef.current(event.latlng.lat, event.latlng.lng)
    })
    marker.on('dragend', () => {
      userMovedPinRef.current = true
      const center = marker.getLatLng()
      circle.setLatLng(center)
      onCenterChangeRef.current(center.lat, center.lng)
    })
    mapRef.current = map
    markerRef.current = marker
    circleRef.current = circle
    return () => {
      map.remove()
      mapRef.current = null
      markerRef.current = null
      circleRef.current = null
      myMarkerRef.current = null
      accuracyRef.current = null
    }
    // Leaflet owns the map after mounting. Later prop changes update its layers below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    markerRef.current?.setLatLng([latitude, longitude])
    circleRef.current?.setLatLng([latitude, longitude])
  }, [latitude, longitude])

  useEffect(() => {
    const circle = circleRef.current
    const map = mapRef.current
    if (!circle || !map) return
    circle.setRadius(radiusMeters)
    map.fitBounds(circle.getBounds(), { padding: [24, 24], maxZoom: 17, animate: false })
  }, [radiusMeters])

  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    if (!myLocation) {
      myMarkerRef.current?.remove()
      accuracyRef.current?.remove()
      myMarkerRef.current = null
      accuracyRef.current = null
      return
    }
    const center = L.latLng(myLocation.latitude, myLocation.longitude)
    if (!accuracyRef.current) {
      accuracyRef.current = L.circle(center, {
        radius: myLocation.accuracy, color: '#3478dd', weight: 1,
        fillColor: '#5196f4', fillOpacity: 0.1, interactive: false,
      }).addTo(map)
    } else {
      accuracyRef.current.setLatLng(center).setRadius(myLocation.accuracy)
    }
    if (!myMarkerRef.current) {
      myMarkerRef.current = L.circleMarker(center, {
        radius: 8, color: '#fff', weight: 3,
        fillColor: '#3478dd', fillOpacity: 1, bubblingMouseEvents: false,
      }).addTo(map).bindTooltip('Your location')
    } else {
      myMarkerRef.current.setLatLng(center)
    }
  }, [myLocation])

  useEffect(() => {
    let active = true
    let permission: PermissionStatus | null = null

    const stopWatch = () => {
      if (watchIdRef.current !== null && navigator.geolocation) {
        navigator.geolocation.clearWatch(watchIdRef.current)
        watchIdRef.current = null
      }
    }
    const startWatch = () => {
      if (!navigator.geolocation || watchIdRef.current !== null) return
      watchIdRef.current = navigator.geolocation.watchPosition(position => {
        if (!active) return
        const centerPin = !centeredOnLocationRef.current && !userMovedPinRef.current
        centeredOnLocationRef.current = true
        usePosition(position, centerPin)
      }, error => {
        if (!active) return
        if (error.code === error.PERMISSION_DENIED) {
          stopWatch()
          setMyLocation(null)
        }
      }, { enableHighAccuracy: true, maximumAge: 30_000, timeout: 15_000 })
    }
    startWatchRef.current = startWatch

    // Querying permission does not prompt. No geolocation call happens while
    // permission is still "prompt"; the map button is the only request path.
    const checkExistingPermission = async () => {
      if (!navigator.geolocation || !navigator.permissions?.query) return
      try {
        permission = await navigator.permissions.query({ name: 'geolocation' })
        if (!active) return
        const syncPermission = () => {
          if (permission?.state === 'granted') startWatch()
          else {
            stopWatch()
            setMyLocation(null)
          }
        }
        permission.addEventListener('change', syncPermission)
        syncPermission()
        removePermissionListener = () => permission?.removeEventListener('change', syncPermission)
      } catch {
        // Unsupported permission querying must not trigger a location prompt.
      }
    }
    let removePermissionListener = () => {}
    void checkExistingPermission()
    return () => {
      active = false
      removePermissionListener()
      stopWatch()
      startWatchRef.current = null
    }
  }, [usePosition])

  useEffect(() => {
    setRadiusInput(String(radiusMeters))
  }, [radiusMeters])

  useEffect(() => () => searchAbortRef.current?.abort(), [])

  async function searchPlace() {
    const query = placeQuery.trim()
    if (query.length < 2) {
      setPlaceMessage('Enter at least two characters.')
      return
    }
    searchAbortRef.current?.abort()
    const controller = new AbortController()
    searchAbortRef.current = controller
    setSearchingPlace(true)
    setPlaceMessage(null)
    setPlaceResults([])
    try {
      const results = await api.locations(query, controller.signal)
      if (controller.signal.aborted) return
      setPlaceResults(results)
      if (results.length === 0) setPlaceMessage('No matching places found. Try a more specific name.')
    } catch (error) {
      if (!controller.signal.aborted) setPlaceMessage((error as Error).message)
    } finally {
      if (!controller.signal.aborted) setSearchingPlace(false)
    }
  }

  function choosePlace(place: LocationSearchResult) {
    movePin(place.latitude, place.longitude, true)
    setPlaceQuery(place.displayName)
    setPlaceResults([])
    setPlaceMessage(null)
  }

  function centerOnMyLocation() {
    if (!navigator.geolocation) {
      setLocationMessage('Location is unavailable in this browser.')
      return
    }
    setLocating(true)
    setLocationMessage(null)
    navigator.geolocation.getCurrentPosition(position => {
      centeredOnLocationRef.current = true
      usePosition(position, true)
      startWatchRef.current?.()
      setLocating(false)
    }, error => {
      setLocating(false)
      setLocationMessage(error.code === error.PERMISSION_DENIED
        ? 'Location access is blocked. Allow it in your browser settings and try again.'
        : 'Could not determine your location. Try again.')
    }, { enableHighAccuracy: true, maximumAge: 30_000, timeout: 15_000 })
  }

  function editRadius(value: string) {
    setRadiusInput(value)
    if (!/^\d+$/.test(value)) return
    const nextRadius = Number(value)
    if (nextRadius >= minimumRadiusMeters && nextRadius <= maximumTypedRadiusMeters)
      onRadiusChange(nextRadius)
  }

  function finishRadiusEdit() {
    if (radiusInput.trim() === '') {
      setRadiusInput(String(radiusMeters))
      return
    }
    const value = Number(radiusInput)
    if (!Number.isFinite(value)) {
      setRadiusInput(String(radiusMeters))
      return
    }
    const nextRadius = Math.max(minimumRadiusMeters, Math.min(maximumTypedRadiusMeters, Math.round(value)))
    setRadiusInput(String(nextRadius))
    onRadiusChange(nextRadius)
  }

  function jumpTo(name: string) {
    const preset = presets.find(item => item.name === name)
    if (!preset) return
    movePin(preset.latitude, preset.longitude, true)
  }

  return <div className="map-selector">
    <div className="map-toolbar"><span>Click the map or drag the pin</span><select aria-label="Jump to a city" defaultValue="" onChange={event => jumpTo(event.target.value)}><option value="" disabled>Jump to a city</option>{presets.map(preset => <option key={preset.name} value={preset.name}>{preset.name}</option>)}</select></div>
    <div className="map-stage">
      <div ref={containerRef} className="search-map" aria-label="Choose the search center on the map" />
      <div className="map-search-overlay">
        <div className="map-place-search" role="search">
          <input type="search" value={placeQuery} onChange={event => { searchAbortRef.current?.abort(); setSearchingPlace(false); setPlaceQuery(event.target.value); setPlaceResults([]); setPlaceMessage(null) }} onKeyDown={event => { if (event.key === 'Escape') setPlaceResults([]); if (event.key === 'Enter') { event.preventDefault(); void searchPlace() } }} placeholder="Search a place, e.g. Baga Beach, Goa" aria-label="Search for a place" maxLength={120} />
          <button type="button" onClick={() => void searchPlace()} disabled={searchingPlace}>{searchingPlace ? 'Searching…' : 'Search'}</button>
        </div>
        {placeResults.length > 0 && <div className="map-place-results" role="listbox" aria-label="Matching places">{placeResults.map(place => <button type="button" role="option" aria-selected="false" key={place.id} onClick={() => choosePlace(place)}>{place.displayName}</button>)}<small>Search data © OpenStreetMap contributors</small></div>}
        {placeMessage && <div className="map-place-message" role="status">{placeMessage}</div>}
      </div>
      <button type="button" className="map-my-location" onClick={centerOnMyLocation} disabled={locating} aria-label="Center map on my location" title="Center map on my location">
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true"><path d="M12 2v3m0 14v3M2 12h3m14 0h3M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10Zm0 3a2 2 0 1 0 0 4 2 2 0 0 0 0-4Z" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" /></svg>
      </button>
      {locationMessage && <div className="map-location-message" role="status">{locationMessage}</div>}
    </div>
    <div className="map-controls">
      <div className="map-radius-header">
        <label htmlFor="radius-input">Search radius</label>
        <div className="map-radius-entry"><input id="radius-input" type="number" inputMode="numeric" min={minimumRadiusMeters} max={maximumTypedRadiusMeters} step="1" required value={radiusInput} onChange={event => editRadius(event.target.value)} onBlur={finishRadiusEdit} aria-describedby="radius-input-limits" /><span>m</span></div>
      </div>
      <input id="radius-range" type="range" min={minimumRadiusMeters} max={maximumSliderRadiusMeters} step="1" value={Math.min(radiusMeters, maximumSliderRadiusMeters)} onChange={event => onRadiusChange(Number(event.target.value))} aria-label="Search radius slider" />
      <div className="map-range-labels"><span>100 m</span><span>10 km</span></div>
      <div id="radius-input-limits" className="map-input-hint">Type a radius up to 50 km.</div>
    </div>
    <div className="map-coordinates">Center: {latitude.toFixed(5)}, {longitude.toFixed(5)}{myLocation && <span className="map-location-visible"><i /> Your location is shown in blue</span>}</div>
  </div>
}
