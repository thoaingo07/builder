import type { BuildStatus, DeploymentStatus, JobStatus } from '@/api/types'

export function relativeTime(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return '—'
  const diff = (now - new Date(iso).getTime()) / 1000
  const abs = Math.abs(diff)
  const suffix = diff >= 0 ? 'ago' : 'from now'
  if (abs < 10) return 'just now'
  if (abs < 60) return `${Math.round(abs)}s ${suffix}`
  if (abs < 3600) return `${Math.round(abs / 60)}m ${suffix}`
  if (abs < 86400) return `${Math.round(abs / 3600)}h ${suffix}`
  if (abs < 86400 * 30) return `${Math.round(abs / 86400)}d ${suffix}`
  return new Date(iso).toLocaleDateString()
}

export function duration(start: string | null | undefined, end: string | null | undefined, now = Date.now()): string {
  if (!start) return '—'
  const ms = (end ? new Date(end).getTime() : now) - new Date(start).getTime()
  return formatMs(ms)
}

export function formatMs(ms: number): string {
  if (ms < 0) ms = 0
  const s = Math.floor(ms / 1000)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ${s % 60}s`
  const h = Math.floor(m / 60)
  return `${h}h ${m % 60}m`
}

export function bytes(n: number | null | undefined): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let v = n
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v.toFixed(v < 10 && i > 0 ? 1 : 0)} ${units[i]}`
}

export function pct(used: number, total: number): number {
  return total > 0 ? Math.min(100, Math.max(0, (used / total) * 100)) : 0
}

export const shortSha = (sha: string | null | undefined) => (sha ? sha.slice(0, 8) : '—')

export function dateTime(iso: string | null | undefined) {
  return iso ? new Date(iso).toLocaleString() : '—'
}

export type UiColor = 'primary' | 'secondary' | 'success' | 'info' | 'warning' | 'error' | 'neutral'
type AnyStatus = BuildStatus | JobStatus | DeploymentStatus

export function statusColor(s: AnyStatus): UiColor {
  switch (s) {
    case 'Succeeded': case 'Active': return 'success'
    case 'Failed': return 'error'
    case 'Running': case 'Assigned': case 'Deploying': case 'Destroying': return 'info'
    case 'WaitingApproval': case 'Canceling': return 'warning'
    case 'Planning': case 'Queued': return 'primary'
    default: return 'neutral'
  }
}

export function statusIcon(s: AnyStatus): { name: string; spin: boolean } {
  switch (s) {
    case 'Succeeded': case 'Active': return { name: 'i-lucide-circle-check', spin: false }
    case 'Failed': return { name: 'i-lucide-circle-x', spin: false }
    case 'Running': case 'Assigned': case 'Deploying': case 'Destroying': case 'Canceling': case 'Planning':
      return { name: 'i-lucide-loader-circle', spin: true }
    case 'WaitingApproval': return { name: 'i-lucide-lock', spin: false }
    case 'Queued': case 'Pending': return { name: 'i-lucide-clock', spin: false }
    case 'Skipped': return { name: 'i-lucide-skip-forward', spin: false }
    case 'Canceled': return { name: 'i-lucide-circle-slash', spin: false }
    case 'Destroyed': return { name: 'i-lucide-trash-2', spin: false }
    default: return { name: 'i-lucide-circle', spin: false }
  }
}

/** CSS color for a Nuxt UI semantic color (there is no --ui-neutral variable). */
export function cssColor(c: UiColor): string {
  return c === 'neutral' ? 'var(--ui-text-dimmed)' : `var(--ui-${c})`
}

export const statusCssColor = (s: AnyStatus) => cssColor(statusColor(s))

export const isBuildActive = (s: BuildStatus) => s === 'Planning' || s === 'Running' || s === 'Canceling'

export function humanize(s: string) {
  return s.replace(/([a-z])([A-Z])/g, '$1 $2')
}
