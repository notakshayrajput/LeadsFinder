import { useCallback, useMemo, useState } from 'react'
import { RapidGrid, type ColumnOptions, type FormatItemEventArgs, type RapidGridCore } from 'rapid-grid-react'
import 'rapid-grid-react/style.css'
import type { SourceRecord } from './api'
import { formatDate, sourceLabel } from './jobUi'

const columns: ColumnOptions[] = [
  { binding: 'name', header: 'Place', width: 280, readOnly: true },
  { binding: 'address', header: 'Address', width: 310, readOnly: true },
  { binding: 'contact', header: 'Contact details', width: 230, readOnly: true },
  { binding: 'source', header: 'Source', width: 190, readOnly: true },
]

function webUrl(value: string | null) {
  if (!value) return null
  try {
    const url = new URL(value.includes('://') ? value : `https://${value}`)
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null
  } catch { return null }
}

function textElement(tag: string, value: string) {
  const element = document.createElement(tag)
  element.textContent = value
  return element
}

function sourceLink(label: string, value: string | null) {
  const href = webUrl(value)
  if (!href) return textElement('span', label)
  const link = document.createElement('a')
  link.textContent = label
  link.href = href
  link.target = '_blank'
  link.rel = 'noopener noreferrer'
  return link
}

export default function SavedRowsGrid({ records }: { records: SourceRecord[] }) {
  const [rawId, setRawId] = useState<string | null>(null)
  const rawRecord = records.find(record => record.id === rawId)
  const items = useMemo(() => records.map(record => ({
    name: record.name, address: record.address,
    contact: [record.phone, record.email, record.website].filter(Boolean).join(' · '),
    source: sourceLabel(record.strategy), record,
  })), [records])

  const formatCell = useCallback((grid: RapidGridCore, event: FormatItemEventArgs) => {
    if (event.panel !== grid.cells || !event.dataItem) return
    const record = event.dataItem.record as SourceRecord
    const cell = event.cellElement
    // The engine recycles these elements; rebuild content from this row without mounting React roots.
    cell.replaceChildren()
    cell.title = ''
    switch (event.col) {
      case 0: {
        const name = textElement('strong', record.name)
        name.title = record.name
        const button = document.createElement('button')
        button.type = 'button'
        button.textContent = 'View raw observation'
        button.setAttribute('aria-label', `View raw observation for ${record.name}`)
        button.onclick = () => setRawId(record.id)
        cell.append(name, textElement('small', `${record.latitude.toFixed(5)}, ${record.longitude.toFixed(5)}`), button)
        break
      }
      case 1: {
        const address = textElement('span', record.address || 'Address unavailable')
        address.className = 'grid-address'
        address.title = record.address
        cell.append(address)
        break
      }
      case 2:
        if (record.phone) cell.append(textElement('span', record.phone))
        if (record.email) cell.append(textElement('span', record.email))
        if (record.website) cell.append(sourceLink('Website ↗', record.website))
        if (!cell.childNodes.length) cell.append(textElement('span', 'No contact details from source'))
        break
      case 3:
        cell.append(sourceLink(`${sourceLabel(record.strategy)} ↗`, record.sourceLink), textElement('small', formatDate(record.fetchedAtUtc)))
        break
    }
  }, [])

  return <>
    <p className="job-grid-help">Scroll to browse rows and columns. Select cells to copy their values.</p>
    <RapidGrid className="job-records-grid" aria-label="Saved observations" items={items} columns={columns}
      rowHeight={96} rowHeaderWidth={42} columnHeaderHeight={38} overscanRows={2} overscanColumns={1}
      style={{ height: Math.min(520, 38 + records.length * 96 + 18), width: '100%' }}
      onFormatItem={formatCell} />
    {rawRecord && <section className="job-raw-inspector" aria-label="Raw observation inspector">
      <div><h3>Raw observation: {rawRecord.name}</h3><button type="button" onClick={() => setRawId(null)}>Close raw observation</button></div>
      <pre>{rawRecord.rawJson}</pre>
    </section>}
  </>
}
