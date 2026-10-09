<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import type { DropdownMenuItem, TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { PipelineDto } from '@/api/types'
import { relativeTime } from '@/lib/format'
import { upsert } from '@/lib/collections'
import { useLiveStore } from '@/stores/live'
import { useOrgStore } from '@/stores/org'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import { useNow } from '@/composables/useNow'
import StatusBadge from '@/components/StatusBadge.vue'
import RunnerEditModal from '@/components/pipelines/RunnerEditModal.vue'
import RunPipelineModal from '@/components/pipelines/RunPipelineModal.vue'

// "Runners": each one is a mapped runner file (.builder/runners/*.yml) in a repository.
const router = useRouter()
const live = useLiveStore()
const org = useOrgStore()
const notify = useNotify()
const confirm = useConfirm()
const now = useNow(10000)

const runners = ref<PipelineDto[]>([])
const loading = ref(true)
const editOpen = ref(false)
const runOpen = ref(false)
const editing = ref<PipelineDto | null>(null)
const running = ref<PipelineDto | null>(null)

const columns: TableColumn<PipelineDto>[] = [
  { accessorKey: 'name', header: 'Runner' },
  { id: 'repo', header: 'Repository' },
  { id: 'last', header: 'Last build' },
  { id: 'actions', header: '' },
]

async function load() {
  try { runners.value = await api.pipelines.list() } catch (e) { notify.error(e, 'Could not load runners') } finally { loading.value = false }
}

function edit(p: PipelineDto) { editing.value = p; editOpen.value = true }
function run(p: PipelineDto) { running.value = p; runOpen.value = true }

async function unmap(p: PipelineDto) {
  if (!await confirm({
    title: 'Unmap runner',
    message: `Unmap "${p.name}"? Its builds are deleted. ${p.taskfilePath} stays in the repository and can be mapped again.`,
    confirmLabel: 'Unmap', danger: true,
  })) return
  try {
    await api.pipelines.remove(p.id)
    runners.value = runners.value.filter(x => x.id !== p.id)
    notify.success(`Unmapped ${p.name}`)
  } catch (e) { notify.error(e, 'Unmap failed') }
}

function menu(p: PipelineDto): DropdownMenuItem[][] {
  const groups: DropdownMenuItem[][] = [[
    { label: 'Edit runner file', icon: 'i-lucide-file-code-2', onSelect: () => void router.push(`/pipelines/${p.id}/editor`) },
    { label: 'Builds', icon: 'i-lucide-hammer', onSelect: () => void router.push({ path: '/builds', query: { pipelineId: p.id } }) },
    { label: 'Repository', icon: 'i-lucide-folder-git-2', onSelect: () => void router.push(`/repositories/${p.repositoryId}`) },
  ]]
  if (org.isAdmin) {
    groups[0].splice(1, 0, { label: 'Name & entry task', icon: 'i-lucide-pencil', onSelect: () => edit(p) })
    groups.push([{ label: 'Unmap', icon: 'i-lucide-unlink', color: 'error', onSelect: () => void unmap(p) }])
  }
  return groups
}

let off: (() => void) | undefined
onMounted(() => {
  void load()
  off = live.onBuild(b => {
    const p = runners.value.find(x => x.id === b.pipelineId)
    if (p && (!p.lastBuild || p.lastBuild.number <= b.number)) runners.value = upsert(runners.value, { ...p, lastBuild: b })
  })
})
onBeforeUnmount(() => off?.())
</script>

<template>
  <UDashboardPanel id="runners">
    <template #header>
      <UDashboardNavbar title="Runners" icon="i-lucide-workflow">
        <template #right>
          <UButton v-if="org.isAdmin" icon="i-lucide-link-2" label="Map runner files" color="neutral" variant="outline" to="/repositories" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !runners.length" icon="i-lucide-workflow" title="No runners yet"
        description="A runner is a Taskfile in a repository's .builder/runners/ folder. Add the repository, then map its runner files."
        :actions="[{ label: 'Repositories', icon: 'i-lucide-folder-git-2', to: '/repositories' }]"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <UTable :data="runners" :columns="columns" :loading="loading">
          <template #name-cell="{ row }">
            <RouterLink :to="`/pipelines/${row.original.id}/editor`" class="font-medium text-highlighted hover:text-primary">{{ row.original.name }}</RouterLink>
            <div v-if="row.original.entryTask" class="font-mono text-xs text-muted">→ {{ row.original.entryTask }}</div>
          </template>
          <template #repo-cell="{ row }">
            <RouterLink :to="`/repositories/${row.original.repositoryId}`" class="text-sm hover:text-primary">{{ row.original.repositoryName }}</RouterLink>
            <div class="flex items-center gap-1 font-mono text-xs text-muted">
              {{ row.original.taskfilePath }} · <UIcon name="i-lucide-git-branch" />{{ row.original.defaultBranch }}
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

      <RunnerEditModal v-model:open="editOpen" :runner="editing" @saved="p => (runners = upsert(runners, p, false))" />
      <RunPipelineModal v-model:open="runOpen" :pipeline="running" />
    </template>
  </UDashboardPanel>
</template>
