<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { TableColumn, TabsItem } from '@nuxt/ui'
import { api, ApiError } from '@/api/client'
import type { ArtifactDto, BuildDetailDto, DeploymentDto, JobDto } from '@/api/types'
import { bytes, dateTime, duration, isBuildActive, relativeTime, shortSha } from '@/lib/format'
import { upsert } from '@/lib/collections'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import { useBuildActions } from '@/composables/useBuildActions'
import StatusBadge from '@/components/StatusBadge.vue'
import ReasonBadge from '@/components/ReasonBadge.vue'
import DataList from '@/components/DataList.vue'
import NavAction from '@/components/NavAction.vue'
import { useIsMobile } from '@/composables/useIsMobile'
import JobProgress from '@/components/JobProgress.vue'
import JobGraph from '@/components/graph/JobGraph.vue'
import JobPanel from '@/components/JobPanel.vue'

const props = defineProps<{ id: string }>()

const route = useRoute()
const router = useRouter()
const live = useLiveStore()
const notify = useNotify()
const now = useNow()
const actions = useBuildActions()

const build = ref<BuildDetailDto | null>(null)
const loading = ref(true)
const notFound = ref(false)
const mobile = useIsMobile()
// phones: the list reads better than a zoomed-out graph
const view = ref<'graph' | 'list'>(mobile.value ? 'list' : 'graph')

const selectedId = computed<string | null>({
  get: () => (typeof route.query.job === 'string' ? route.query.job : null),
  set: v => { void router.replace({ query: { ...route.query, job: v ?? undefined } }) },
})
const selectedJob = computed(() => build.value?.jobs.find(j => j.id === selectedId.value) ?? null)
const panelOpen = computed({
  get: () => !!selectedJob.value,
  set: v => { if (!v) selectedId.value = null },
})

const waiting = computed(() => build.value?.jobs.filter(j => j.status === 'WaitingApproval') ?? [])
const sortedJobs = computed(() => [...(build.value?.jobs ?? [])].sort((a, b) => a.order - b.order))
const variables = computed(() => Object.entries(build.value?.variables ?? {}))

const tabs: TabsItem[] = [
  { label: 'Graph', icon: 'i-lucide-workflow', value: 'graph' },
  { label: 'List', icon: 'i-lucide-list', value: 'list' },
]

async function load() {
  try {
    build.value = await api.builds.get(props.id)
    notFound.value = false
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) notFound.value = true
    else notify.error(e, 'Could not load build')
  } finally {
    loading.value = false
  }
}

function applyJob(j: JobDto) {
  const b = build.value
  if (!b || j.buildId !== b.id) return
  if (!b.jobs.some(x => x.id === j.id)) { void load(); return } // graph was just planned
  b.jobs = b.jobs.map(x => (x.id === j.id ? j : x))
}

function selectKey(key: string) {
  const j = build.value?.jobs.find(x => x.key === key)
  if (j) selectedId.value = j.id
}

async function cancel() { if (build.value && await actions.cancel(build.value)) void load() }
async function remove() { if (build.value && await actions.remove(build.value)) void router.push('/builds') }
const canDestroy = (st: DeploymentDto['status']) => st === 'Active' || st === 'Failed' || st === 'Superseded' || st === 'RolledBack'
async function rollback(d: DeploymentDto) {
  const r = await actions.rollbackDeployment(d)
  if (r && build.value) build.value.deployments = upsert(build.value.deployments, r)
}
async function destroy(d: DeploymentDto) {
  const r = await actions.destroyDeployment(d)
  if (r && build.value) build.value.deployments = upsert(build.value.deployments, r)
}

const jobColumns: TableColumn<JobDto>[] = [
  { accessorKey: 'status', header: 'Status' },
  { accessorKey: 'taskName', header: 'Task' },
  { id: 'deps', header: 'Depends on' },
  { accessorKey: 'agentName', header: 'Agent' },
  { id: 'duration', header: 'Duration' },
]
const artifactColumns: TableColumn<ArtifactDto>[] = [
  { accessorKey: 'name', header: 'Name' },
  { id: 'job', header: 'Job' },
  { id: 'size', header: 'Size' },
  { id: 'created', header: 'Created' },
]
const deploymentColumns: TableColumn<DeploymentDto>[] = [
  { accessorKey: 'status', header: 'Status' },
  { accessorKey: 'name', header: 'App' },
  { accessorKey: 'environmentName', header: 'Environment' },
  { id: 'updated', header: 'Updated' },
  { id: 'actions', header: '' },
]
const jobName = (id: string) => build.value?.jobs.find(j => j.id === id)?.taskName ?? '—'

const offs: (() => void)[] = []
onMounted(() => {
  void load()
  void live.joinBuild(props.id)
  offs.push(live.onBuild(s => {
    const b = build.value
    if (!b || s.id !== b.id) return
    const wasPlanning = b.status === 'Planning'
    build.value = { ...b, ...s }
    if (wasPlanning && s.status !== 'Planning') void load()
  }))
  offs.push(live.onJob(applyJob))
  offs.push(live.onDeployment(d => {
    if (build.value && d.buildId === build.value.id) build.value.deployments = upsert(build.value.deployments, d)
  }))
})
onBeforeUnmount(() => {
  offs.forEach(f => f())
  void live.leaveBuild(props.id)
})

watch(() => props.id, (next, prev) => {
  void live.leaveBuild(prev)
  void live.joinBuild(next)
  loading.value = true
  build.value = null
  void load()
})
</script>

<template>
  <UDashboardPanel id="build">
    <template #header>
      <UDashboardNavbar :title="build ? `${build.pipelineName} #${build.number}` : 'Build'">
        <template #leading>
          <UButton icon="i-lucide-arrow-left" color="neutral" variant="ghost" to="/builds" aria-label="Back to builds" />
        </template>
        <template #trailing>
          <StatusBadge v-if="build" :status="build.status" />
          <span v-if="build" class="max-sm:hidden"><ReasonBadge :reason="build.reason" :pull-request-id="build.pullRequestId" :by="build.requestedBy" /></span>
        </template>
        <template #right>
          <template v-if="build">
            <NavAction
              v-if="isBuildActive(build.status)" icon="i-lucide-square" label="Cancel" color="error" variant="outline"
              :disabled="build.status === 'Canceling'" :loading="actions.busy.value === `cancel:${build.id}`" @click="cancel"
            />
            <NavAction
              icon="i-lucide-rotate-ccw" label="Re-run" color="neutral" variant="outline"
              :loading="actions.busy.value === `rerun:${build.id}`" @click="actions.rerun(build)"
            />
            <UButton
              v-if="!isBuildActive(build.status)" icon="i-lucide-trash-2" color="error" variant="ghost" aria-label="Delete build"
              :loading="actions.busy.value === `delete:${build.id}`" @click="remove"
            />
          </template>
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div v-if="loading" class="space-y-4">
        <USkeleton class="h-24 w-full" />
        <USkeleton class="h-96 w-full" />
      </div>

      <UEmpty
        v-else-if="notFound || !build" icon="i-lucide-search-x" title="Build not found"
        description="It may have been deleted by a cleanup." :actions="[{ label: 'All builds', to: '/builds' }]"
      />

      <template v-else>
        <UCard :ui="{ body: 'sm:p-4 p-3' }">
          <div class="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-6">
            <div>
              <div class="text-xs text-muted">Branch</div>
              <div class="flex items-center gap-1 truncate text-sm font-medium"><UIcon name="i-lucide-git-branch" class="shrink-0" />{{ build.branch }}</div>
              <UTooltip v-if="build.sourceRef" :text="`Built from ${build.sourceRef}`">
                <div class="truncate font-mono text-[11px] text-muted">{{ build.sourceRef }}</div>
              </UTooltip>
            </div>
            <div>
              <div class="text-xs text-muted">Commit</div>
              <UTooltip :text="build.commit ?? 'Resolving…'">
                <div class="flex items-center gap-1 font-mono text-sm"><UIcon name="i-lucide-git-commit-horizontal" />{{ shortSha(build.commit) }}</div>
              </UTooltip>
            </div>
            <div>
              <div class="text-xs text-muted">Entry task</div>
              <div class="font-mono text-sm">{{ build.entryTask || '—' }}</div>
            </div>
            <div>
              <div class="text-xs text-muted">Requested by</div>
              <span class="sm:hidden"><ReasonBadge :reason="build.reason" :pull-request-id="build.pullRequestId" :by="build.requestedBy" size="xs" /></span>
              <div class="text-sm">{{ build.requestedBy }} · <span class="text-muted">{{ relativeTime(build.queuedAt, now) }}</span></div>
            </div>
            <div>
              <div class="text-xs text-muted">Duration</div>
              <div class="text-sm tabular-nums">{{ duration(build.startedAt, build.finishedAt, now) }}</div>
            </div>
            <div>
              <div class="text-xs text-muted">Jobs</div>
              <JobProgress :counts="build.jobCounts" class="mt-1" />
            </div>
          </div>
          <div v-if="variables.length" class="mt-3 flex flex-wrap gap-1.5 border-t border-default pt-3">
            <UBadge v-for="[k, v] in variables" :key="k" color="neutral" variant="soft" class="font-mono">{{ k }}={{ v }}</UBadge>
          </div>
        </UCard>

        <UAlert v-if="build.error" color="error" variant="subtle" icon="i-lucide-circle-x" title="Build failed" :description="build.error" />
        <UAlert v-if="build.status === 'Planning'" color="primary" variant="subtle" icon="i-lucide-loader-circle" title="Planning" description="Resolving the commit and reading the Taskfile…" :ui="{ icon: 'animate-spin' }" />

        <UAlert
          v-for="j in waiting" :key="j.id" color="warning" variant="subtle" icon="i-lucide-lock" orientation="horizontal"
          :title="`${j.taskName} is waiting for approval`" :description="j.approval?.message"
          :actions="[{ label: 'Review', color: 'warning', variant: 'solid', onClick: () => { selectedId = j.id } }]"
        />

        <UCard :ui="{ header: 'sm:px-4 px-3 py-2', body: 'p-0 sm:p-0' }">
          <template #header>
            <div class="flex items-center gap-2">
              <h2 class="flex-1 font-semibold text-highlighted">Jobs</h2>
              <UTabs v-model="view" :items="tabs" :content="false" size="xs" variant="pill" />
            </div>
          </template>
          <div v-if="!build.jobs.length" class="p-6">
            <UEmpty icon="i-lucide-workflow" :title="build.status === 'Planning' ? 'Planning…' : 'No jobs'" variant="naked" />
          </div>
          <div v-else-if="view === 'graph'" class="h-[60vh] min-h-80 md:h-[440px]">
            <JobGraph :jobs="build.jobs" :selected-id="selectedId" @select="id => (selectedId = id)" />
          </div>
          <DataList v-else :data="sortedJobs" :columns="jobColumns" clickable @select="j => (selectedId = j.id)">
            <template #card="{ item: j }">
              <div class="flex items-start gap-2">
                <div class="min-w-0 flex-1 space-y-1">
                  <div class="flex flex-wrap items-center gap-1.5">
                    <StatusBadge :status="j.status" kind="job" size="sm" />
                    <span class="truncate font-mono font-medium text-highlighted">{{ j.taskName }}</span>
                    <UIcon v-if="j.approval" name="i-lucide-lock" class="text-warning" />
                    <UIcon v-if="j.deploy" name="i-lucide-rocket" class="text-primary" />
                  </div>
                  <div class="flex flex-wrap gap-x-2 text-xs text-muted">
                    <span v-if="j.agentName" class="font-mono">{{ j.agentName }}</span>
                    <span v-if="j.startedAt" class="tabular-nums">{{ duration(j.startedAt, j.finishedAt, now) }}</span>
                    <span v-if="j.steps?.length">{{ j.steps.filter(x => x.status === 'Succeeded' || x.status === 'Failed' || x.status === 'Skipped').length }}/{{ j.steps.length }} steps</span>
                    <span v-if="j.dependsOn.length" class="truncate">after {{ j.dependsOn.join(', ') }}</span>
                  </div>
                  <div v-if="j.error && j.status === 'Failed'" class="truncate text-xs text-error" :title="j.error">{{ j.error }}</div>
                </div>
                <UIcon name="i-lucide-chevron-right" class="mt-1 shrink-0 text-dimmed" />
              </div>
            </template>
            <template #status-cell="{ row }"><StatusBadge :status="row.original.status" size="sm" /></template>
            <template #taskName-cell="{ row }">
              <span class="font-mono font-medium">{{ row.original.taskName }}</span>
              <UIcon v-if="row.original.approval" name="i-lucide-lock" class="ml-1 text-warning" />
              <UIcon v-if="row.original.deploy" name="i-lucide-rocket" class="ml-1 text-primary" />
            </template>
            <template #deps-cell="{ row }"><span class="font-mono text-xs text-muted">{{ row.original.dependsOn.join(', ') || '—' }}</span></template>
            <template #agentName-cell="{ row }"><span class="font-mono text-xs">{{ row.original.agentName ?? '—' }}</span></template>
            <template #duration-cell="{ row }"><span class="tabular-nums">{{ duration(row.original.startedAt, row.original.finishedAt, now) }}</span></template>
          </DataList>
        </UCard>

        <UCard v-if="build.deployments.length" :ui="{ header: 'sm:px-4 px-3 py-3', body: 'p-0 sm:p-0' }">
          <template #header><h2 class="font-semibold text-highlighted">Deployments</h2></template>
          <DataList :data="build.deployments" :columns="deploymentColumns">
            <template #card="{ item: d }">
              <div class="space-y-1.5">
                <div class="flex flex-wrap items-center gap-1.5">
                  <StatusBadge :status="d.status" size="sm" />
                  <span class="font-mono font-medium">{{ d.name }}</span>
                  <span class="text-xs text-muted">· {{ d.environmentName }} · {{ relativeTime(d.updatedAt ?? d.createdAt, now) }}</span>
                </div>
                <div class="flex flex-wrap gap-1">
                  <UButton v-if="d.url && d.status === 'Active'" :to="d.url" target="_blank" size="xs" variant="soft" label="Open app" trailing-icon="i-lucide-external-link" />
                  <UButton
                    v-if="d.status === 'Active' && d.isContainer" size="xs" color="warning" variant="ghost" icon="i-lucide-undo-2" label="Roll back"
                    :loading="actions.busy.value === `rollback:${d.id}`" @click="rollback(d)"
                  />
                  <UButton
                    v-if="canDestroy(d.status)" size="xs" color="error" variant="ghost"
                    icon="i-lucide-trash-2" label="Destroy" :loading="actions.busy.value === `destroy:${d.id}`" @click="destroy(d)"
                  />
                </div>
              </div>
            </template>
            <template #status-cell="{ row }"><StatusBadge :status="row.original.status" size="sm" /></template>
            <template #updated-cell="{ row }"><span class="text-xs text-muted">{{ relativeTime(row.original.updatedAt ?? row.original.createdAt, now) }}</span></template>
            <template #actions-cell="{ row }">
              <div class="flex justify-end gap-1">
                <UButton v-if="row.original.url && row.original.status === 'Active'" :to="row.original.url" target="_blank" size="xs" variant="soft" label="Open app" trailing-icon="i-lucide-external-link" />
                <UButton
                  v-if="row.original.status === 'Active' && row.original.isContainer" size="xs" color="warning" variant="ghost" icon="i-lucide-undo-2" label="Roll back"
                  :loading="actions.busy.value === `rollback:${row.original.id}`" @click="rollback(row.original)"
                />
                <UButton
                  v-if="canDestroy(row.original.status)" size="xs" color="error" variant="ghost"
                  icon="i-lucide-trash-2" label="Destroy" :loading="actions.busy.value === `destroy:${row.original.id}`" @click="destroy(row.original)"
                />
              </div>
            </template>
          </DataList>
        </UCard>

        <UCard v-if="build.artifacts.length" :ui="{ header: 'sm:px-4 px-3 py-3', body: 'p-0 sm:p-0' }">
          <template #header><h2 class="font-semibold text-highlighted">Artifacts</h2></template>
          <DataList :data="build.artifacts" :columns="artifactColumns">
            <template #card="{ item: a }">
              <a :href="api.artifactUrl(a.id)" download class="flex items-center gap-2 text-sm">
                <UIcon name="i-lucide-download" class="shrink-0 text-muted" />
                <span class="min-w-0 flex-1 truncate" :title="a.name">{{ a.name }}</span>
                <span class="shrink-0 text-xs text-muted">{{ bytes(a.sizeBytes) }} · {{ jobName(a.jobId) }}</span>
              </a>
            </template>
            <template #name-cell="{ row }">
              <a :href="api.artifactUrl(row.original.id)" download class="flex items-center gap-1.5 hover:text-primary">
                <UIcon name="i-lucide-download" />{{ row.original.name }}
              </a>
            </template>
            <template #job-cell="{ row }"><span class="font-mono text-xs">{{ jobName(row.original.jobId) }}</span></template>
            <template #size-cell="{ row }">{{ bytes(row.original.sizeBytes) }}</template>
            <template #created-cell="{ row }"><span class="text-xs text-muted">{{ dateTime(row.original.createdAt) }}</span></template>
          </DataList>
        </UCard>
      </template>

      <USlideover
        v-model:open="panelOpen" :title="selectedJob?.taskName ?? ''" :description="selectedJob?.key !== selectedJob?.taskName ? selectedJob?.key : undefined"
        side="right" :ui="{ content: 'max-w-3xl w-full', body: 'flex flex-col min-h-0' }"
      >
        <template #body>
          <JobPanel
            v-if="selectedJob && build" :build-id="build.id" :job="selectedJob" :artifacts="build.artifacts"
            @job-updated="applyJob" @select-key="selectKey"
          />
        </template>
      </USlideover>
    </template>
  </UDashboardPanel>
</template>
