<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { api } from '@/api/client'
import type { LogLineDto } from '@/api/types'
import { parseAnsi, type AnsiSegment } from '@/lib/ansi'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ buildId: string; jobId: string; active: boolean }>()

const MAX_LINES = 20000
const PAGE = 5000

interface Rendered { id: number; stream: LogLineDto['stream']; time: string; segments: AnsiSegment[]; raw: string }

const live = useLiveStore()
const notify = useNotify()
const lines = shallowRef<Rendered[]>([])
const truncated = ref(false)
const loading = ref(false)
const follow = ref(true)
const filter = ref('')
const showTime = ref(false)
const scroller = ref<HTMLElement | null>(null)
let lastId = 0
let pending: LogLineDto[] | null = null // live lines that arrive while the initial fetch runs
let generation = 0

function render(l: LogLineDto): Rendered {
  return {
    id: l.id, stream: l.stream, raw: l.text,
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

const visible = computed(() => {
  const q = filter.value.trim().toLowerCase()
  return q ? lines.value.filter(l => l.raw.toLowerCase().includes(q)) : lines.value
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
      <span class="text-xs text-muted tabular-nums">{{ visible.length }} lines</span>
      <UIcon v-if="active" name="i-lucide-radio" class="size-3.5 animate-pulse text-info" />
      <span class="flex-1" />
      <USwitch v-model="showTime" size="xs" label="Time" />
      <USwitch v-model="follow" size="xs" label="Follow" />
      <UTooltip text="Copy log">
        <UButton icon="i-lucide-copy" size="xs" color="neutral" variant="ghost" @click="copyAll" />
      </UTooltip>
    </div>
    <div
      ref="scroller" class="log min-h-0 flex-1 overflow-auto py-2 font-mono text-xs leading-5"
      @scroll.passive="onScroll"
    >
      <div v-if="truncated" class="px-3 pb-1 text-[11px] text-amber-300">Showing the last {{ MAX_LINES.toLocaleString() }} lines.</div>
      <div v-if="loading && !lines.length" class="px-3 text-slate-400">Loading…</div>
      <div v-else-if="!lines.length" class="px-3 text-slate-500">{{ active ? 'Waiting for output…' : 'No output.' }}</div>
      <div v-for="l in visible" :key="l.id" class="log-line" :class="l.stream.toLowerCase()">
        <span v-if="showTime" class="log-time">{{ l.time }}</span>
        <span class="log-text"><span v-for="(s, i) in l.segments" :key="i" :class="s.cls">{{ s.text }}</span></span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.log { background: var(--log-bg); color: var(--log-text); }
.log-line { display: flex; gap: 10px; padding: 0 12px; white-space: pre-wrap; word-break: break-all; }
.log-line:hover { background: rgb(255 255 255 / 4%); }
.log-line.err .log-text { color: #ff8b85; }
.log-line.system .log-text { color: #7d8796; font-style: italic; }
.log-time { color: #5c6573; flex-shrink: 0; user-select: none; }
</style>
