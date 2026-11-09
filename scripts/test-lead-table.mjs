// Render the actual table with old API payloads to cover upgrade compatibility.
import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const dashboard = path.join(root, 'src/LeadsFinder/LeadsFinder.Dashboard')
const require = createRequire(path.join(dashboard, 'package.json'))
const ts = require('typescript')
const React = require('react')
const { renderToStaticMarkup } = require('react-dom/server')
await mkdir(path.join(root, 'artifacts'), { recursive: true })
const output = await mkdtemp(path.join(root, 'artifacts/table-compat-'))
for (const file of ['api.ts', 'LeadTable.tsx']) {
  let code = ts.transpileModule(await readFile(path.join(dashboard, 'src', file), 'utf8'), {
    compilerOptions: { jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
  }).outputText
  for (const specifier of ['react/jsx-runtime', 'react']) {
    const url = pathToFileURL(require.resolve(specifier)).href
    code = code.replaceAll(`from "${specifier}"`, `from "${url}"`).replaceAll(`from '${specifier}'`, `from '${url}'`)
  }
  code = code.replace("from './api'", "from './api.mjs'")
  await writeFile(path.join(output, file.replace(/\.tsx?$/, '.mjs')), code)
}
const { default: LeadTable } = await import(pathToFileURL(path.join(output, 'LeadTable.mjs')).href)
const legacy = { id: 'old-record', name: 'Existing hotel', locality: 'New Delhi', phone: null, email: null, website: null }
for (const changes of [{}, { sales: null, fieldQuality: null }, { sales: { stage: 'possible_sale' }, fieldQuality: { phone: null } }]) {
  const html = renderToStaticMarkup(React.createElement(LeadTable, { leads: [{ ...legacy, ...changes }] }))
  assert.ok(html.includes('Existing hotel'))
  assert.ok(html.includes('Details'))
  assert.ok(html.includes('WhatsApp'))
}
console.log('PASS: LeadTable renders legacy, null, and partial sales/field-quality payloads without crashing.')
