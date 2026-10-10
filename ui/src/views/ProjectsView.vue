<script setup lang="ts">
import { onMounted, ref } from 'vue'
import type { TableColumn } from '@nuxt/ui'
import { api, ApiError } from '@/api/client'
import type { ProjectDto } from '@/api/types'
import { dateTime } from '@/lib/format'
import { useOrgStore } from '@/stores/org'
import { useProjectStore } from '@/stores/project'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import { useHighlight } from '@/composables/useHighlight'
import DataList from '@/components/DataList.vue'
import NavAction from '@/components/NavAction.vue'
import ProjectFormModal from '@/components/org/ProjectFormModal.vue'

const org = useOrgStore()
const store = useProjectStore()
const notify = useNotify()
const confirm = useConfirm()
const hl = useHighlight()
const loading = ref(true)
const formOpen = ref(false)
const editing = ref<ProjectDto | null>(null)

const columns: TableColumn<ProjectDto>[] = [
  { accessorKey: 'name', header: 'Project' },
  { id: 'counts', header: 'Contents' },
  { id: 'created', header: 'Created' },
  { id: 'actions', header: '' },
]

const counts = (p: ProjectDto) => [
  { icon: 'i-lucide-folder-git-2', n: p.repositoryCount, label: 'repositories' },
  { icon: 'i-lucide-workflow', n: p.runnerCount, label: 'runners' },
  { icon: 'i-lucide-plug', n: p.connectionCount, label: 'connections' },
  { icon: 'i-lucide-cloud', n: p.environmentCount, label: 'environments' },
  { icon: 'i-lucide-key-round', n: p.secretCount, label: 'secrets' },
]

async function load() {
  loading.value = true
  try { await store.load() } finally { loading.value = false }
}

function create() { editing.value = null; formOpen.value = true }
function edit(p: ProjectDto) { if (!org.isAdmin) return; editing.value = p; formOpen.value = true }
function saved(p: ProjectDto) { void hl.flash(p.id) }

async function remove(p: ProjectDto) {
  if (!await confirm({
    title: 'Delete project',
    message: `Delete "${p.name}"? Only an empty project can be deleted — move or remove its repositories, connections, environments and secrets first.`,
    confirmLabel: 'Delete', danger: true,
  })) return
  try {
    await api.projects.remove(p.id)
    store.removeLocal(p.id)
    notify.success(`Deleted ${p.name}`)
  } catch (e) {
    // 409 explains what still lives in the project
    if (e instanceof ApiError && e.status === 409) notify.error(e, 'Project is not empty')
    else notify.error(e, 'Delete failed')
  }
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="projects">
    <template #header>
      <UDashboardNavbar title="Projects" icon="i-lucide-folder-kanban">
        <template #right>
          <NavAction v-if="org.isAdmin" icon="i-lucide-plus" label="New project" @click="create" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <p class="text-sm text-muted">
        Projects group repositories and their runners. Connections, environments and secrets can belong to one project or be shared by all;
        a runner uses its project's item first, then the shared one with the same name.
      </p>
      <UEmpty
        v-if="!loading && !store.projects.length" icon="i-lucide-folder-kanban" title="No projects yet"
        :description="org.isAdmin ? 'Create your first project, then add repositories to it.' : 'An admin can create projects for this organization.'"
        :actions="org.isAdmin ? [{ label: 'New project', icon: 'i-lucide-plus', onClick: create }] : []"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <DataList :data="store.projects" :columns="columns" :loading="loading" :highlight-id="hl.id.value" :clickable="org.isAdmin" @select="edit">
          <template #card="{ item: p }">
            <div class="flex items-start gap-2">
              <UIcon name="i-lucide-folder-kanban" class="mt-0.5 shrink-0 text-muted" />
              <div class="min-w-0 flex-1 space-y-1">
                <div class="flex items-center gap-1.5">
                  <span class="truncate font-medium text-highlighted">{{ p.name }}</span>
                  <UBadge v-if="p.id === store.currentId" label="Selected" size="sm" color="primary" variant="subtle" />
                </div>
                <p v-if="p.description" class="truncate text-xs text-muted">{{ p.description }}</p>
                <div class="flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted">
                  <span v-for="c in counts(p)" :key="c.label" class="flex items-center gap-1" :title="c.label"><UIcon :name="c.icon" />{{ c.n }}</span>
                </div>
              </div>
              <UDropdownMenu
                :content="{ align: 'end' }"
                :items="[
                  [{ label: 'Open', icon: 'i-lucide-arrow-right', onSelect: () => store.select(p.id) }],
                  ...(org.isAdmin ? [[{ label: 'Edit', icon: 'i-lucide-pencil', onSelect: () => edit(p) }], [{ label: 'Delete', icon: 'i-lucide-trash-2', color: 'error' as const, onSelect: () => remove(p) }]] : []),
                ]"
              >
                <UButton icon="i-lucide-ellipsis-vertical" size="xs" color="neutral" variant="ghost" aria-label="More actions" @click.stop />
              </UDropdownMenu>
            </div>
          </template>
          <template #name-cell="{ row }">
            <div class="flex items-center gap-1.5">
              <span class="font-medium text-highlighted">{{ row.original.name }}</span>
              <UBadge v-if="row.original.id === store.currentId" label="Selected" size="sm" color="primary" variant="subtle" />
            </div>
            <div v-if="row.original.description" class="max-w-md truncate text-xs text-muted">{{ row.original.description }}</div>
          </template>
          <template #counts-cell="{ row }">
            <div class="flex flex-wrap gap-x-3 text-xs text-muted">
              <UTooltip v-for="c in counts(row.original)" :key="c.label" :text="`${c.n} ${c.label}`">
                <span class="flex items-center gap-1"><UIcon :name="c.icon" />{{ c.n }}</span>
              </UTooltip>
            </div>
          </template>
          <template #created-cell="{ row }"><span class="text-xs text-muted">{{ dateTime(row.original.createdAt) }}</span></template>
          <template #actions-cell="{ row }">
            <div class="flex justify-end gap-1" @click.stop>
              <UButton
                :label="row.original.id === store.currentId ? 'Selected' : 'Open'" size="xs" color="neutral" variant="outline"
                :disabled="row.original.id === store.currentId" @click="store.select(row.original.id)"
              />
              <UButton v-if="org.isAdmin" icon="i-lucide-trash-2" size="xs" color="error" variant="ghost" aria-label="Delete project" @click="remove(row.original)" />
            </div>
          </template>
        </DataList>
      </UCard>
      <ProjectFormModal v-model:open="formOpen" :project="editing" @saved="saved" />
    </template>
  </UDashboardPanel>
</template>
