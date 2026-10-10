<script setup lang="ts">
import { computed } from 'vue'
import type { PullRequestTrigger, PushTrigger, ScheduleDto, TriggerSpec } from '@/api/types'
import { describeCron } from '@/lib/cron'
import { dateTime, relativeTime } from '@/lib/format'
import { useNow } from '@/composables/useNow'

/** Read-only summary of x-builder.triggers; `runs` adds next/last run times when the server knows them. */
const props = defineProps<{ spec: TriggerSpec; defaultBranch: string; runs?: ScheduleDto[] | null }>()
const now = useNow(30000)

const filters = computed(() => [
  { key: 'push', icon: 'i-lucide-git-commit-horizontal', title: 'Push', branchLabel: 'Branches', value: props.spec.push as PushTrigger | null },
  { key: 'pr', icon: 'i-lucide-git-pull-request', title: 'Pull request', branchLabel: 'Target branches', value: props.spec.pullRequest as PullRequestTrigger | null },
])

const schedules = computed(() => props.spec.schedules.map((s, i) => {
  const runs = props.runs ?? null
  const st = runs ? (runs.find(x => x.cron === s.cron && x.branch === s.branch && x.timeZone === s.timeZone) ?? runs[i] ?? null) : null
  return { ...s, describe: describeCron(s.cron), nextRunAt: st?.nextRunAt ?? null, lastRunAt: st?.lastRunAt ?? null }
}))
</script>

<template>
  <div class="space-y-3">
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
            <div v-if="runs" class="mt-1 flex flex-wrap gap-x-4 text-muted">
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

  </div>
</template>
