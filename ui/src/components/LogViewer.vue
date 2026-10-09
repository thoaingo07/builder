<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, shallowRef, watch } from 'vue'
import { api } from '@/api/client'
import type { JobStepDto, LogLineDto } from '@/api/types'
import { parseAnsi, type AnsiSegment } from '@/lib/ansi'
import { cssColor, duration, stepIcon } from '@/lib/format'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'

const props = withDefaults(defineProps<{
  buildId: string; jobId: string; active: boolean
  steps?: JobStepDto[]; jobFailed?: boolean
  /** step index to reveal (from the step timeline); null = none */
  focusStep?: number | null
}>(), { steps: () => [], jobFailed: false, focusStep: null })

const MAX_LINES = 20000
const PAGE = 5000
const SETUP = 'setup'

interface Rendered { id: number; step: number | null; stream: LogLineDto['stream']; time: string; segments: AnsiSegment[]; raw: string }
type GroupKey = number | typeof SETUP

const live = useLiveStore()
const notify = useNotify()
const now = useNow()
const lines = shallowRef<Rendered[]>([])
const truncated = ref(false)
const loading = ref(false)
const follow = ref(true)
const filter = ref('')
const showTime = ref(false)
const scroller = ref<HTMLElement | null>(null)
/** user expand/collapse choices; otherwise the defaults below apply */
const overrides = reactive(new Map<GroupKey, boolean>())
let lastId = 0
let pending: LogLineDto[] | null = null // live lines that arrive while the initial fetch runs
let generation = 0

function render(l: LogLineDto): Rendered {
  return {
    id: l.id, step: l.step ?? null, stream: l.stream, raw: l.text,
    time: new Date(l.timestamp).toLocaleTimeString(undefined, { hour12: false }),
    segments: parseAnsi(l.text),
  }
}

function append(batch: LogLineDto[]) {
  const fresh = batch.filter(l => l.id > lastId).sort((a, b) => a.id - b.id)
  if (!fresh.length) return
  lastId = fresh[fresh.length - 1].id
  let next = lines.value.concat(fresh.map(render))
  if (next.length > MAX_LINES) { next = next.slice(next.length - MAX_LINES); truncated.value = true }
  lines.value = next
  if (follow.value) void nextTick(scrollToEnd)
}

async function load() {
  const gen = ++generation
  lines.value = []
  truncated.value = false
  lastId = 0
  pending = []
  overrides.clear()
  loading.value = true
  try {
    for (;;) {
      const page = await api.builds.logs(props.buildId, props.jobId, lastId)
      if (gen !== generation) return
      append(page)
      if (page.length < PAGE) break
    }
  } catch (e) {
    notify.error(e, 'Could not load logs')
  } finally {
    if (gen === generation) {
      const buffered = pending ?? []
      pending = null
      append(buffered)
      loading.value = false
    }
  }
}

function scrollToEnd() {
  const el = scroller.value
  if (el) el.scrollTop = el.scrollHeight
}

function onScroll() {
  const el = scroller.value
  if (!el) return
  const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40
  if (!atBottom && follow.value && !loading.value) follow.value = false
}

watch(follow, f => { if (f) void nextTick(scrollToEnd) })
watch(() => props.jobId, () => { follow.value = true; void load() })

// ---------- grouping ----------
const query = computed(() => filter.value.trim().toLowerCase())
const stepByIndex = computed(() => new Map(props.steps.map(s => [s.index, s])))

interface Group { key: GroupKey; step: JobStepDto | null; title: string; lines: Rendered[]; total: number }
const groups = computed<Group[]>(() => {
  const byKey = new Map<GroupKey, Rendered[]>()
  for (const l of lines.value) {
    const k: GroupKey = l.step ?? SETUP
    const arr = byKey.get(k)
    if (arr) arr.push(l)
    else byKey.set(k, [l])
  }
  // setup first, then steps in order; steps that started but printed nothing still get a header
  const keys: GroupKey[] = []
  if (byKey.has(SETUP)) keys.push(SETUP)
  const stepKeys = new Set<number>([...byKey.keys()].filter((k): k is number => k !== SETUP))
  for (const s of props.steps) if (s.status !== 'Pending') stepKeys.add(s.index)
  keys.push(...[...stepKeys].sort((a, b) => a - b))

  const q = query.value
  return keys.map(k => {
    const all = byKey.get(k) ?? []
    const step = k === SETUP ? null : (stepByIndex.value.get(k) ?? null)
    return {
      key: k, step, total: all.length,
      title: k === SETUP ? 'Setup' : (step?.label ?? `Step ${k + 1}`),
      lines: q ? all.filter(l => l.raw.toLowerCase().includes(q)) : all,
    }
  }).filter(g => !q || g.lines.length)
})

function defaultExpanded(g: Group): boolean {
  if (props.active) return true
  if (g.step) return g.step.status === 'Failed' || g.step.status === 'Running'
  // setup: open when the job failed before (or without) a failing step
  return props.jobFailed && !props.steps.some(s => s.status === 'Failed')
}
const isExpanded = (g: Group) => (query.value ? true : (overrides.get(g.key) ?? defaultExpanded(g)))
function toggle(g: Group) { overrides.set(g.key, !isExpanded(g)) }

const visibleCount = computed(() => groups.value.reduce((n, g) => n + g.lines.length, 0))

// reveal a step chosen in the timeline
const groupEls = new Map<GroupKey, HTMLElement>()
function setGroupEl(key: GroupKey, el: unknown) {
  if (el instanceof HTMLElement) groupEls.set(key, el)
  else groupEls.delete(key)
}
watch(() => props.focusStep, async idx => {
  if (idx === null || idx === undefined) return
  follow.value = false
  overrides.set(idx, true)
  await nextTick()
  groupEls.get(idx)?.scrollIntoView({ block: 'start', behavior: 'smooth' })
})

function copyAll() {
  void navigator.clipboard?.writeText(lines.value.map(l => l.raw).join('\n'))
    .then(() => notify.success('Log copied'))
}

let off: (() => void) | undefined
onMounted(() => {
  off = live.onLog((buildId, batch) => {
    if (buildId !== props.buildId) return
    const mine = batch.filter(l => l.jobId === props.jobId)
    if (!mine.length) return
    if (pending) pending.push(...mine)
    else append(mine)
  })
  void load()
})
onBeforeUnmount(() => { off?.(); generation++ })
</script>

<template>
  <div class="flex h-full min-h-0 flex-col overflow-hidden rounded-lg border border-default">
    <div class="flex flex-wrap items-center gap-2 border-b border-default bg-elevated/50 px-2 py-1.5">
      <UInput v-model="filter" icon="i-lucide-search" placeholder="Filter lines" size="xs" class="w-44" />
      <span class="text-xs text-muted tabular-nums">{{ visibleCount }} lines</span>
      <UIcon v-if="active" name="i-lucide-radio" class="size-3.5 animate-pulse text-info" />
      <span class="flex-1" />
      <USwitch v-model="showTime" size="xs" label="Time" />
      <USwitch v-model="follow" size="xs" label="Follow" />
      <UTooltip text="Copy log">
        <UButton icon="i-lucide-copy" size="xs" color="neutral" variant="ghost" @click="copyAll" />
      </UTooltip>
    </div>
    <div ref="scroller" class="log min-h-0 flex-1 overflow-auto pb-2 font-mono text-xs leading-5" @scroll.passive="onScroll">
      <div v-if="truncated" class="px-3 pt-1 text-[11px] text-amber-300">Showing the last {{ MAX_LINES.toLocaleString() }} lines.</div>
      <div v-if="loading && !lines.length" class="px-3 pt-2 text-slate-400">Loading…</div>
      <div v-else-if="!groups.length" class="px-3 pt-2 text-slate-500">
        {{ query ? 'No matching lines.' : active ? 'Waiting for output…' : 'No output.' }}
      </div>

      <section v-for="g in groups" :key="g.key" :ref="el => setGroupEl(g.key, el)" class="log-group" :class="{ focused: focusStep === g.key }">
        <button type="button" class="log-head" :aria-expanded="isExpanded(g)" @click="toggle(g)">
          <UIcon :name="isExpanded(g) ? 'i-lucide-chevron-down' : 'i-lucide-chevron-right'" class="size-3.5 shrink-0 text-slate-500" />
          <UIcon
            v-if="g.step" :name="stepIcon(g.step.status).name" class="size-3.5 shrink-0"
            :class="stepIcon(g.step.status).spin ? 'animate-spin' : ''" :style="{ color: cssColor(stepIcon(g.step.status).color) }"
          />
          <UIcon v-else name="i-lucide-settings-2" class="size-3.5 shrink-0 text-slate-500" />
          <span class="truncate">{{ g.title }}</span>
          <span class="flex-1" />
          <span class="shrink-0 text-slate-500 tabular-nums">{{ query ? `${g.lines.length}/${g.total}` : g.total }}</span>
          <span v-if="g.step?.startedAt" class="w-14 shrink-0 text-right text-slate-500 tabular-nums">{{ duration(g.step.startedAt, g.step.finishedAt, now) }}</span>
        </button>
        <template v-if="isExpanded(g)">
          <div v-for="l in g.lines" :key="l.id" class="log-line" :class="l.stream.toLowerCase()">
            <span v-if="showTime" class="log-time">{{ l.time }}</span>
            <span class="log-text"><span v-for="(s, i) in l.segments" :key="i" :class="s.cls">{{ s.text }}</span></span>
          </div>
        </template>
      </section>
    </div>
  </div>
</template>

<style scoped>
.log { background: var(--log-bg); color: var(--log-text); }
.log-head {
  position: sticky; top: 0; z-index: 1; display: flex; width: 100%; align-items: center; gap: 6px;
  padding: 3px 10px; background: color-mix(in srgb, var(--log-bg) 88%, white); color: #c4ccd8;
  border-bottom: 1px solid rgb(255 255 255 / 6%); text-align: left; cursor: pointer;
}
.log-head:hover { background: color-mix(in srgb, var(--log-bg) 80%, white); }
.log-group.focused .log-head { box-shadow: inset 2px 0 0 var(--ui-primary); }
.log-line { display: flex; gap: 10px; padding: 0 12px 0 30px; white-space: pre-wrap; word-break: break-all; }
.log-line:hover { background: rgb(255 255 255 / 4%); }
.log-line.err .log-text { color: #ff8b85; }
.log-line.system .log-text { color: #7d8796; font-style: italic; }
.log-time { color: #5c6573; flex-shrink: 0; user-select: none; }
</style>
