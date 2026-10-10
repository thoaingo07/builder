<script setup lang="ts">
import ProjectBadge from './ProjectBadge.vue'
import { useRouter } from 'vue-router'
import type { TableColumn } from '@nuxt/ui'
import type { BuildSummaryDto } from '@/api/types'
import { duration, isBuildActive, relativeTime, shortSha } from '@/lib/format'
import { useNow } from '@/composables/useNow'
import { useBuildActions } from '@/composables/useBuildActions'
import StatusBadge from './StatusBadge.vue'
import JobProgress from './JobProgress.vue'
import ReasonBadge from './ReasonBadge.vue'
import DataList from './DataList.vue'

withDefaults(defineProps<{ builds: BuildSummaryDto[]; loading?: boolean; compact?: boolean; empty?: string }>(), {
  loading: false, compact: false, empty: 'No builds yet',
})

const router = useRouter()
const now = useNow()
const actions = useBuildActions()

const columns: TableColumn<BuildSummaryDto>[] = [
  { accessorKey: 'status', header: 'Status' },
  { id: 'build', header: 'Build' },
  { id: 'source', header: 'Source' },
  { id: 'progress', header: 'Jobs' },
  { accessorKey: 'requestedBy', header: 'By' },
  { id: 'when', header: 'Queued' },
  { id: 'duration', header: 'Duration' },
  { id: 'actions', header: '' },
]

function open(b: BuildSummaryDto) {
  void router.push(`/builds/${b.id}`)
}
</script>

<template>
  <DataList :data="builds" :columns="columns" :loading="loading" :empty="empty" clickable @select="open">
    <template #card="{ item: b }">
      <div class="flex items-start gap-2">
        <div class="min-w-0 flex-1 space-y-1">
          <div class="flex flex-wrap items-center gap-1.5">
            <StatusBadge :status="b.status" size="sm" />
            <span class="truncate font-medium text-highlighted">{{ b.pipelineName }} <span class="text-muted">#{{ b.number }}</span></span>
          </div>
          <div class="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted">
            <ReasonBadge :reason="b.reason" :pull-request-id="b.pullRequestId" :by="b.requestedBy" size="xs" />
            <ProjectBadge :project-id="b.projectId" />
            <span class="flex min-w-0 items-center gap-1"><UIcon name="i-lucide-git-branch" class="shrink-0" /><span class="truncate" :title="b.branch">{{ b.branch }}</span></span>
            <span>{{ relativeTime(b.queuedAt, now) }} · {{ duration(b.startedAt, b.finishedAt, now) }}</span>
          </div>
          <div v-if="b.error" class="truncate text-xs text-error" :title="b.error">{{ b.error }}</div>
          <JobProgress :counts="b.jobCounts" />
        </div>
        <div @click.stop>
          <UButton
            v-if="isBuildActive(b.status)" icon="i-lucide-square" color="error" variant="ghost" size="sm" aria-label="Cancel"
            :loading="actions.busy.value === `cancel:${b.id}`" :disabled="b.status === 'Canceling'" @click="actions.cancel(b)"
          />
          <UButton
            v-else-if="!compact" icon="i-lucide-rotate-ccw" color="neutral" variant="ghost" size="sm" aria-label="Re-run"
            :loading="actions.busy.value === `rerun:${b.id}`" @click="actions.rerun(b)"
          />
        </div>
      </div>
    </template>
    <template #status-cell="{ row }">
      <StatusBadge :status="row.original.status" size="sm" />
    </template>
    <template #build-cell="{ row }">
      <div class="flex items-center gap-2">
        <span class="font-medium text-highlighted">{{ row.original.pipelineName }} <span class="text-muted">#{{ row.original.number }}</span></span>
        <ReasonBadge :reason="row.original.reason" :pull-request-id="row.original.pullRequestId" :by="row.original.requestedBy" size="xs" />
        <ProjectBadge :project-id="row.original.projectId" />
      </div>
      <div v-if="row.original.error" class="max-w-64 truncate text-xs text-error">{{ row.original.error }}</div>
    </template>
    <template #source-cell="{ row }">
      <div class="flex items-center gap-1 text-xs">
        <UIcon name="i-lucide-git-branch" class="size-3.5 text-muted" />
        <span class="max-w-40 truncate">{{ row.original.branch }}</span>
      </div>
      <div class="font-mono text-xs text-muted">{{ shortSha(row.original.commit) }} · {{ row.original.entryTask || 'default' }}</div>
    </template>
    <template #progress-cell="{ row }">
      <JobProgress :counts="row.original.jobCounts" />
    </template>
    <template #when-cell="{ row }">
      <UTooltip :text="new Date(row.original.queuedAt).toLocaleString()">
        <span class="text-xs whitespace-nowrap text-muted">{{ relativeTime(row.original.queuedAt, now) }}</span>
      </UTooltip>
    </template>
    <template #duration-cell="{ row }">
      <span class="text-xs whitespace-nowrap tabular-nums">{{ duration(row.original.startedAt, row.original.finishedAt, now) }}</span>
    </template>
    <template #actions-cell="{ row }">
      <div class="flex justify-end gap-1" @click.stop>
        <UTooltip v-if="isBuildActive(row.original.status)" text="Cancel">
          <UButton
            icon="i-lucide-square" color="error" variant="ghost" size="xs"
            :loading="actions.busy.value === `cancel:${row.original.id}`"
            :disabled="row.original.status === 'Canceling'" @click="actions.cancel(row.original)"
          />
        </UTooltip>
        <UTooltip v-else-if="!compact" text="Re-run">
          <UButton
            icon="i-lucide-rotate-ccw" color="neutral" variant="ghost" size="xs"
            :loading="actions.busy.value === `rerun:${row.original.id}`" @click="actions.rerun(row.original)"
          />
        </UTooltip>
      </div>
    </template>
  </DataList>
</template>
