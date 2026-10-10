<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api/client'
import type { PipelineTriggersDto, PullRequestTrigger, PushTrigger } from '@/api/types'
import { describeCron } from '@/lib/cron'
import { dateTime, relativeTime, shortSha } from '@/lib/format'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'

/** Read-only view of a runner's x-builder.triggers as the server last read them. */
const props = defineProps<{ pipelineId: string; defaultBranch: string; initial?: PipelineTriggersDto | null }>()
const emit = defineEmits<{ loaded: [PipelineTriggersDto] }>()
const notify = useNotify()
const now = useNow(30000)

const data = ref<PipelineTriggersDto | null>(props.initial ?? null)
const loading = ref(false)
const refreshing = ref(false)

async function load() {
  loading.value = true
  try {
    data.value = await api.pipelines.triggers(props.pipelineId)
    emit('loaded', data.value)
  } catch (e) { notify.error(e, 'Could not load triggers') } finally { loading.value = false }
}

async function refresh() {
  refreshing.value = true
  try {
    data.value = await api.pipelines.refreshTriggers(props.pipelineId)
    emit('loaded', data.value)
    notify.success('Triggers re-read', `${props.defaultBranch} @ ${shortSha(data.value.commit)}`)
  } catch (e) { notify.error(e, 'Refresh failed') } finally { refreshing.value = false }
}

const filters = computed(() => {
  const t = data.value?.triggers
  return [
    { key: 'push', icon: 'i-lucide-git-commit-horizontal', title: 'Push', branchLabel: 'Branches', value: t?.push ?? null as PushTrigger | null },
    { key: 'pr', icon: 'i-lucide-git-pull-request', title: 'Pull request', branchLabel: 'Target branches', value: t?.pullRequest ?? null as PullRequestTrigger | null },
  ]
})

const schedules = computed(() => {
  const specs = data.value?.triggers.schedules ?? []
  const state = data.value?.schedules ?? []
  return specs.map((s, i) => {
    const st = state.find(x => x.cron === s.cron && x.branch === s.branch && x.timeZone === s.timeZone) ?? state[i] ?? null
    return { ...s, describe: describeCron(s.cron), nextRunAt: st?.nextRunAt ?? null, lastRunAt: st?.lastRunAt ?? null }
  })
})

watch(() => props.pipelineId, () => { data.value = null; void load() })
onMounted(() => { if (!data.value) void load() })
</script>

<template>
  <div class="space-y-4">
    <div class="flex items-center gap-2 text-xs text-muted">
      <UIcon name="i-lucide-git-branch" />
      <span>read from <strong class="text-default">{{ defaultBranch }}</strong> @ <code>{{ shortSha(data?.commit) }}</code></span>
      <span class="flex-1" />
      <UButton icon="i-lucide-refresh-cw" label="Refresh" size="xs" color="neutral" variant="outline" :loading="refreshing" @click="refresh" />
    </div>

    <USkeleton v-if="loading && !data" class="h-32 w-full" />
    <template v-else-if="data">
      <UAlert v-if="data.error" color="error" variant="subtle" icon="i-lucide-circle-x" title="The runner file's triggers are invalid" :description="data.error" />

      <div v-for="f in filters" :key="f.key" class="rounded-md border border-default p-3">
        <div class="flex items-center gap-2">
          <UIcon :name="f.icon" class="text-muted" />
          <span class="text-sm font-medium">{{ f.title }}</span>
          <span class="flex-1" />
          <UBadge :label="f.value ? 'On' : 'Off'" :color="f.value ? 'success' : 'neutral'" variant="subtle" size="sm" />
        </div>
        <dl v-if="f.value" class="mt-2 grid grid-cols-[auto_1fr] items-start gap-x-3 gap-y-1.5 text-xs">
          <dt class="pt-0.5 text-muted">{{ f.branchLabel }}</dt>
          <dd class="flex flex-wrap gap-1">
            <UBadge v-for="b in f.value.branches" :key="b" :label="b" size="sm" color="neutral" variant="soft" class="font-mono" />
            <span v-if="!f.value.branches.length" class="text-muted">all branches</span>
          </dd>
          <template v-if="f.value.paths.length">
            <dt class="pt-0.5 text-muted">Paths</dt>
            <dd class="flex flex-wrap gap-1">
              <UBadge
                v-for="p in f.value.paths" :key="p" :label="p" size="sm" variant="soft" class="font-mono"
                :color="p.startsWith('!') ? 'warning' : 'neutral'"
              />
            </dd>
          </template>
          <template v-if="Object.keys(f.value.vars).length">
            <dt class="pt-0.5 text-muted">Vars</dt>
            <dd class="flex flex-wrap gap-1">
              <UBadge v-for="(v, k) in f.value.vars" :key="k" :label="`${k}=${v}`" size="sm" color="primary" variant="subtle" class="font-mono" />
            </dd>
          </template>
        </dl>
      </div>

      <div class="rounded-md border border-default p-3">
        <div class="flex items-center gap-2">
          <UIcon name="i-lucide-clock" class="text-muted" />
          <span class="text-sm font-medium">Schedules</span>
          <span class="flex-1" />
          <UBadge :label="String(schedules.length)" :color="schedules.length ? 'success' : 'neutral'" variant="subtle" size="sm" />
        </div>
        <ul v-if="schedules.length" class="mt-2 divide-y divide-default">
          <li v-for="(s, i) in schedules" :key="i" class="py-2 text-xs">
            <div class="flex flex-wrap items-center gap-2">
              <code class="rounded bg-elevated px-1.5 py-0.5">{{ s.cron }}</code>
              <span>{{ s.describe ?? 'invalid cron' }}</span>
              <UBadge :label="s.timeZone || 'UTC'" size="sm" color="neutral" variant="outline" />
              <span class="flex items-center gap-1 text-muted"><UIcon name="i-lucide-git-branch" />{{ s.branch || defaultBranch }}</span>
            </div>
            <div class="mt-1 flex flex-wrap gap-x-4 text-muted">
              <UTooltip :text="dateTime(s.nextRunAt)"><span>next {{ s.nextRunAt ? relativeTime(s.nextRunAt, now) : '—' }}</span></UTooltip>
              <UTooltip :text="dateTime(s.lastRunAt)"><span>last {{ s.lastRunAt ? relativeTime(s.lastRunAt, now) : 'never' }}</span></UTooltip>
            </div>
            <div v-if="Object.keys(s.vars).length" class="mt-1 flex flex-wrap gap-1">
              <UBadge v-for="(v, k) in s.vars" :key="k" :label="`${k}=${v}`" size="sm" color="primary" variant="subtle" class="font-mono" />
            </div>
          </li>
        </ul>
        <p v-else class="mt-1 text-xs text-muted">No schedules.</p>
      </div>

      <p class="text-xs text-muted">
        Triggers live in the runner file under <code>x-builder.triggers</code>; edit them in the runner editor and commit to {{ defaultBranch }}.
      </p>
    </template>
  </div>
</template>
