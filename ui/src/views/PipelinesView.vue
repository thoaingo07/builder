<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import type { DropdownMenuItem, TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { PipelineDto } from '@/api/types'
import { relativeTime } from '@/lib/format'
import { upsert } from '@/lib/collections'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import { useNow } from '@/composables/useNow'
import StatusBadge from '@/components/StatusBadge.vue'
import PipelineFormModal from '@/components/pipelines/PipelineFormModal.vue'
import RunPipelineModal from '@/components/pipelines/RunPipelineModal.vue'

const router = useRouter()
const live = useLiveStore()
const notify = useNotify()
const confirm = useConfirm()
const now = useNow(10000)

const pipelines = ref<PipelineDto[]>([])
const loading = ref(true)
const formOpen = ref(false)
const runOpen = ref(false)
const editing = ref<PipelineDto | null>(null)
const running = ref<PipelineDto | null>(null)

const columns: TableColumn<PipelineDto>[] = [
  { accessorKey: 'name', header: 'Pipeline' },
  { id: 'repo', header: 'Repository' },
  { id: 'last', header: 'Last build' },
  { id: 'actions', header: '' },
]

async function load() {
  try { pipelines.value = await api.pipelines.list() } catch (e) { notify.error(e, 'Could not load pipelines') } finally { loading.value = false }
}

function create() { editing.value = null; formOpen.value = true }
function edit(p: PipelineDto) { editing.value = p; formOpen.value = true }
function run(p: PipelineDto) { running.value = p; runOpen.value = true }

async function remove(p: PipelineDto) {
  if (!await confirm({ title: 'Delete pipeline', message: `Delete "${p.name}" and all of its builds? The Taskfile in git is not touched.`, confirmLabel: 'Delete', danger: true })) return
  try {
    await api.pipelines.remove(p.id)
    pipelines.value = pipelines.value.filter(x => x.id !== p.id)
    notify.success(`Deleted ${p.name}`)
  } catch (e) { notify.error(e, 'Delete failed') }
}

function menu(p: PipelineDto): DropdownMenuItem[][] {
  return [
    [
      { label: 'Edit Taskfile', icon: 'i-lucide-workflow', onSelect: () => void router.push(`/pipelines/${p.id}/editor`) },
      { label: 'Settings', icon: 'i-lucide-settings', onSelect: () => edit(p) },
      { label: 'Builds', icon: 'i-lucide-hammer', onSelect: () => void router.push({ path: '/builds', query: { pipelineId: p.id } }) },
    ],
    [{ label: 'Delete', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => void remove(p) }],
  ]
}

let off: (() => void) | undefined
onMounted(() => {
  void load()
  off = live.onBuild(b => {
    const p = pipelines.value.find(x => x.id === b.pipelineId)
    if (p && (!p.lastBuild || p.lastBuild.number <= b.number)) pipelines.value = upsert(pipelines.value, { ...p, lastBuild: b })
  })
})
onBeforeUnmount(() => off?.())
</script>

<template>
  <UDashboardPanel id="pipelines">
    <template #header>
      <UDashboardNavbar title="Pipelines" icon="i-lucide-workflow">
        <template #right>
          <UButton icon="i-lucide-plus" label="New pipeline" @click="create" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !pipelines.length" icon="i-lucide-workflow" title="No pipelines yet"
        description="Point Builder at a repository with a Taskfile.yml to get started."
        :actions="[{ label: 'New pipeline', icon: 'i-lucide-plus', onClick: create }]"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <UTable :data="pipelines" :columns="columns" :loading="loading">
          <template #name-cell="{ row }">
            <RouterLink :to="`/pipelines/${row.original.id}/editor`" class="font-medium text-highlighted hover:text-primary">{{ row.original.name }}</RouterLink>
            <div class="font-mono text-xs text-muted">{{ row.original.taskfilePath }}<template v-if="row.original.entryTask"> → {{ row.original.entryTask }}</template></div>
          </template>
          <template #repo-cell="{ row }">
            <div class="max-w-md truncate font-mono text-xs">{{ row.original.repositoryUrl }}</div>
            <div class="flex items-center gap-1 text-xs text-muted">
              <UIcon name="i-lucide-git-branch" />{{ row.original.defaultBranch }}
              <template v-if="row.original.connectionName"> · <UIcon name="i-lucide-key-round" />{{ row.original.connectionName }}</template>
            </div>
          </template>
          <template #last-cell="{ row }">
            <RouterLink v-if="row.original.lastBuild" :to="`/builds/${row.original.lastBuild.id}`" class="flex items-center gap-2">
              <StatusBadge :status="row.original.lastBuild.status" size="sm" />
              <span class="text-xs text-muted">#{{ row.original.lastBuild.number }} · {{ relativeTime(row.original.lastBuild.queuedAt, now) }}</span>
            </RouterLink>
            <span v-else class="text-xs text-dimmed">Never run</span>
          </template>
          <template #actions-cell="{ row }">
            <div class="flex justify-end gap-1">
              <UButton icon="i-lucide-play" label="Run" size="xs" @click="run(row.original)" />
              <UDropdownMenu :items="menu(row.original)" :content="{ align: 'end' }">
                <UButton icon="i-lucide-ellipsis-vertical" color="neutral" variant="ghost" size="xs" aria-label="More actions" />
              </UDropdownMenu>
            </div>
          </template>
        </UTable>
      </UCard>

      <PipelineFormModal v-model:open="formOpen" :pipeline="editing" @saved="p => (pipelines = upsert(pipelines, p, false))" />
      <RunPipelineModal v-model:open="runOpen" :pipeline="running" />
    </template>
  </UDashboardPanel>
</template>
