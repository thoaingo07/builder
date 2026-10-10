/** Human-readable preview for common cron expressions (5 fields, or 6 with seconds). Returns null when invalid. */
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
const DAY_NAMES: Record<string, number> = { SUN: 0, MON: 1, TUE: 2, WED: 3, THU: 4, FRI: 5, SAT: 6 }
const FIELD = /^(\*|\?|[0-9A-Za-z]+(-[0-9A-Za-z]+)?)(\/\d+)?(,(\*|[0-9A-Za-z]+(-[0-9A-Za-z]+)?)(\/\d+)?)*$/

const pad = (n: string) => n.padStart(2, '0')
const isNum = (s: string) => /^\d+$/.test(s)

function dayName(s: string) {
  const n = isNum(s) ? Number(s) % 7 : DAY_NAMES[s.toUpperCase()]
  return n === undefined ? s : DAYS[n]
}

function list(field: string, map: (s: string) => string) {
  return field.split(',').map(part => part.includes('-') ? part.split('-').map(map).join('–') : map(part)).join(', ')
}

export function cronIsValid(expr: string): boolean {
  const f = expr.trim().split(/\s+/)
  return (f.length === 5 || f.length === 6) && f.every(x => FIELD.test(x))
}

export function describeCron(expr: string): string | null {
  if (!cronIsValid(expr)) return null
  let f = expr.trim().split(/\s+/)
  let sec = ''
  if (f.length === 6) { sec = f[0]; f = f.slice(1) }
  const [min, hour, dom, mon, dow] = f

  let when: string
  if (min.startsWith('*/') && hour === '*') when = `every ${min.slice(2)} minutes`
  else if (min === '*' && hour === '*') when = 'every minute'
  else if (isNum(min) && hour === '*') when = `every hour at :${pad(min)}`
  else if (isNum(min) && hour.startsWith('*/')) when = `every ${hour.slice(2)} hours at :${pad(min)}`
  else if (isNum(min) && /^[\d,]+$/.test(hour)) when = `at ${hour.split(',').map(h => `${pad(h)}:${pad(min)}`).join(', ')}`
  else if (isNum(min) && /^\d+-\d+$/.test(hour)) when = `at :${pad(min)} past every hour from ${pad(hour.split('-')[0])}:00 to ${pad(hour.split('-')[1])}:00`
  else return 'custom schedule'
  if (sec && sec !== '0') when += ` (second ${sec})`

  const anyDom = dom === '*' || dom === '?'
  const anyDow = dow === '*' || dow === '?'
  let days: string
  if (anyDom && anyDow) days = when.startsWith('every') ? '' : 'every day'
  else if (anyDom && (dow === '1-5' || dow.toUpperCase() === 'MON-FRI')) days = 'on weekdays'
  else if (anyDom) days = `on ${list(dow, dayName)}`
  else if (anyDow && /^[\d,-]+$/.test(dom)) days = `on day ${list(dom, x => x)} of the month`
  else return 'custom schedule'
  if (mon !== '*' && mon !== '?') days += ` in ${list(mon, m => (isNum(m) ? MONTHS[Number(m) - 1] ?? m : m))}`

  return [days, when].filter(Boolean).join(' ').replace(/^./, c => c.toUpperCase())
}

/** IANA time zones for the select, UTC first. */
export function timeZones(): string[] {
  let zones: string[] = []
  try {
    zones = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.('timeZone') ?? []
  } catch { /* old browsers */ }
  return ['UTC', ...zones.filter(z => z !== 'UTC')]
}
