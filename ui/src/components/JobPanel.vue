<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ArtifactDto, JobDto } from '@/api/types'
import { api } from '@/api/client'
import { bytes, dateTime, duration, stepProgress } from '@/lib/format'
import { useNow } from '@/composables/useNow'
import StatusBadge from './StatusBadge.vue'
import ApprovalBox from './ApprovalBox.vue'
import LogViewer from './LogViewer.vue'
import StepTimeline from './StepTimeline.vue'

const props = defineProps<{ buildId: string; job: JobDto; artifacts: ArtifactDto[] }>()
const emit = defineEmits<{ jobUpdated: [JobDto]; selectKey: [string] }>()

const now = useNow()
const active = computed(() => ['Assigned', 'Running'].includes(props.job.status))
const hasLog = computed(() => !!props.job.startedAt || active.value || !!props.job.agentName)
const steps = computed(() => props.job.steps ?? [])
const progress = computed(() => stepProgress(steps.value))
/** step picked in the timeline; the log viewer reveals it */
const focusStep = ref<number | null>(null)
watch(() => props.job.id, () => { focusStep.value = null })
const myArtifacts = computed(() => props.artifacts.filter(a => a.jobId === props.job.id))

const facts = computed(() => [
  { label: 'Agent', value: props.job.agentName ?? '—' },
  { label: 'Started', value: dateTime(props.job.startedAt) },
  { label: 'Duration', value: duration(props.job.startedAt, props.job.finishedAt, now.value) },
  { label: 'Exit code', value: props.job.exitCode?.toString() ?? '—' },
])
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-4">
    <div class="flex flex-wrap items-center gap-2">
      <StatusBadge :status="job.status" />
      <span v-if="job.description" class="text-sm text-muted">{{ job.description }}</span>
    </div>

    <div class="grid grid-cols-2 gap-3 sm:grid-cols-4">
      <div v-for="f in facts" :key="f.label">
        <div class="text-xs text-muted">{{ f.label }}</div>
        <div class="truncate text-sm font-medium tabular-nums">{{ f.value }}</div>
      </div>
    </div>

    <div v-if="job.dependsOn.length || job.labels.length || job.secrets?.length" class="flex flex-wrap items-center gap-1.5 text-xs">
      <template v-if="job.dependsOn.length">
        <span class="text-muted">Depends on</span>
        <UButton
          v-for="d in job.dependsOn" :key="d" :label="d" size="xs" color="neutral" variant="soft"
          class="font-mono" @click="emit('selectKey', d)"
        />
      </template>
      <template v-if="job.labels.length">
        <span class="ml-2 text-muted">Agent labels</span>
        <UBadge v-for="l in job.labels" :key="l" :label="l" size="sm" color="neutral" variant="outline" class="font-mono" />
      </template>
      <template v-if="job.secrets?.length">
        <span class="ml-2 text-muted">Secrets</span>
        <UBadge v-for="x in job.secrets" :key="x" :label="x" icon="i-lucide-key-round" size="sm" color="warning" variant="subtle" class="font-mono" />
      </template>
    </div>

    <UAlert v-if="job.error && job.status !== 'Skipped'" color="error" variant="subtle" icon="i-lucide-circle-x" :title="job.error" />
    <UAlert v-else-if="job.status === 'Skipped'" color="neutral" variant="subtle" icon="i-lucide-skip-forward" title="Skipped" :description="job.error ?? 'A dependency did not succeed.'" />

    <ApprovalBox v-if="job.approval" :build-id="buildId" :job="job" @decided="j => emit('jobUpdated', j)" />

    <UCard v-if="job.deploy" :ui="{ body: 'sm:p-3 p-3' }">
      <div class="mb-2 flex items-center gap-2 text-sm font-medium">
        <UIcon name="i-lucide-rocket" class="text-primary" /> Deploys to <span class="font-semibold">{{ job.deploy.environment }}</span>
        <span class="flex-1" />
        <UButton v-if="job.deploy.url" :to="job.deploy.url" target="_blank" size="xs" variant="soft" label="Open app" trailing-icon="i-lucide-external-link" />
      </div>
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 font-mono text-xs">
        <template v-for="(v, k) in { compose: job.deploy.compose, project: job.deploy.project, manifests: job.deploy.manifests, namespace: job.deploy.namespace }" :key="k">
          <template v-if="v"><dt class="text-muted">{{ k }}</dt><dd class="truncate">{{ v }}</dd></template>
        </template>
      </dl>
    </UCard>

    <div v-if="myArtifacts.length || job.artifacts.length" class="space-y-1">
      <div class="text-xs font-medium text-muted">Artifacts <span class="font-mono">{{ job.artifacts.join(', ') }}</span></div>
      <div v-for="a in myArtifacts" :key="a.id" class="flex items-center gap-2 text-sm">
        <UIcon name="i-lucide-package" class="text-muted" />
        <a :href="api.artifactUrl(a.id)" class="hover:text-primary" download>{{ a.name }}</a>
        <span class="text-xs text-muted">{{ bytes(a.sizeBytes) }}</span>
      </div>
    </div>

    <div v-if="steps.length" class="space-y-1">
      <div class="flex items-center gap-2 text-xs font-medium text-muted">
        Steps <span class="tabular-nums">{{ progress.done }}/{{ progress.total }}</span>
        <span class="flex-1" />
        <UButton v-if="focusStep !== null" label="Show all" size="xs" color="neutral" variant="link" @click="focusStep = null" />
      </div>
      <StepTimeline :steps="steps" :now="now" :selected="focusStep" @select="i => (focusStep = i)" />
    </div>

    <div v-if="hasLog" class="min-h-80 flex-1">
      <LogViewer
        :build-id="buildId" :job-id="job.id" :active="active" :steps="steps"
        :job-failed="job.status === 'Failed'" :focus-step="focusStep"
      />
    </div>
    <UEmpty
      v-else-if="!job.approval" icon="i-lucide-clock" title="Not started"
      :description="job.status === 'Queued' ? 'Waiting for a free agent with matching labels.' : 'Waiting for its dependencies.'"
      variant="naked"
    />
  </div>
</template>
