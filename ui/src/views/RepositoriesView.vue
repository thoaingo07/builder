<script setup lang="ts">
import ProjectBadge from '@/components/ProjectBadge.vue'
import { useProjectStore } from '@/stores/project'
import NavAction from '@/components/NavAction.vue'
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import type { TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { RepositoryDto } from '@/api/types'
import { useOrgStore } from '@/stores/org'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import AddRepositoryModal from '@/components/repositories/AddRepositoryModal.vue'
import DataList from '@/components/DataList.vue'

const org = useOrgStore()
const project = useProjectStore()
const router = useRouter()
const notify = useNotify()
const confirm = useConfirm()
const repos = ref<RepositoryDto[]>([])
const loading = ref(true)
const addOpen = ref(false)

const columns: TableColumn<RepositoryDto>[] = [
  { accessorKey: 'name', header: 'Repository' },
  { accessorKey: 'connectionName', header: 'Connection' },
  { accessorKey: 'defaultBranch', header: 'Default branch' },
  { accessorKey: 'runnerCount', header: 'Runners' },
  { id: 'actions', header: '' },
]

async function load() {
  try { repos.value = await api.repositories.list(project.query) } catch (e) { notify.error(e, 'Could not load repositories') } finally { loading.value = false }
}

async function remove(r: RepositoryDto) {
  if (!await confirm({
    title: 'Remove repository',
    message: `Remove "${r.name}" from Builder? Its ${r.runnerCount} runner(s) and all their builds are deleted. The git repository itself is not touched.`,
    confirmLabel: 'Remove', danger: true,
  })) return
  try {
    await api.repositories.remove(r.id)
    repos.value = repos.value.filter(x => x.id !== r.id)
    notify.success(`Removed ${r.name}`)
  } catch (e) { notify.error(e, 'Remove failed') }
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="repositories">
    <template #header>
      <UDashboardNavbar title="Repositories" icon="i-lucide-folder-git-2">
        <template #right>
          <NavAction v-if="org.isAdmin" icon="i-lucide-plus" label="Add repository" @click="addOpen = true" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UEmpty
        v-if="!loading && !repos.length" icon="i-lucide-folder-git-2" title="No repositories yet"
:description="project.projects.length
          ? 'Add a repository, then map its runner files (.builder/runners/*.yml) to runners you can build.'
          : 'No projects yet — create one under Projects first, or add a repository and Builder puts it in a new “Default” project.'"
        :actions="org.isAdmin ? [{ label: 'Add repository', icon: 'i-lucide-plus', onClick: () => { addOpen = true } }, { label: 'Connections', to: '/connections', color: 'neutral', variant: 'outline' }] : []"
      />
      <UCard v-else :ui="{ body: 'p-0 sm:p-0' }">
        <DataList :data="repos" :columns="columns" :loading="loading" clickable @select="r => router.push(`/repositories/${r.id}`)">
          <template #card="{ item: r }">
            <div class="flex items-start gap-2">
              <UIcon name="i-lucide-folder-git-2" class="mt-0.5 shrink-0 text-muted" />
              <div class="min-w-0 flex-1 space-y-0.5">
                <div class="flex items-center gap-1.5">
                  <span class="truncate font-medium text-highlighted">{{ r.name }}</span>
                  <ProjectBadge :project-id="r.projectId" />
                  <UBadge :label="`${r.runnerCount} runner${r.runnerCount === 1 ? '' : 's'}`" :color="r.runnerCount ? 'primary' : 'neutral'" variant="subtle" size="sm" class="shrink-0" />
                </div>
                <div class="truncate font-mono text-xs text-muted" :title="r.url">{{ r.url }}</div>
                <div class="flex items-center gap-1 text-xs text-muted">
                  <UIcon name="i-lucide-git-branch" />{{ r.defaultBranch }} · <UIcon name="i-lucide-plug" />{{ r.connectionName ?? 'public' }}
                </div>
              </div>
              <UDropdownMenu
                v-if="org.isAdmin" :content="{ align: 'end' }"
                :items="[[{ label: 'Runner files', icon: 'i-lucide-file-search', to: `/repositories/${r.id}` }], [{ label: 'Remove', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => remove(r) }]]"
              >
                <UButton icon="i-lucide-ellipsis-vertical" size="xs" color="neutral" variant="ghost" aria-label="More actions" @click.stop />
              </UDropdownMenu>
            </div>
          </template>
          <template #name-cell="{ row }">
            <div class="flex items-center gap-1.5">
              <span class="font-medium text-highlighted">{{ row.original.name }}</span>
              <ProjectBadge :project-id="row.original.projectId" />
            </div>
            <div class="max-w-md truncate font-mono text-xs text-muted">{{ row.original.url }}</div>
          </template>
          <template #connectionName-cell="{ row }">
            <span class="text-sm" :class="row.original.connectionName ? '' : 'text-muted'">{{ row.original.connectionName ?? 'public' }}</span>
          </template>
          <template #defaultBranch-cell="{ row }">
            <span class="flex items-center gap-1 text-sm"><UIcon name="i-lucide-git-branch" class="text-muted" />{{ row.original.defaultBranch }}</span>
          </template>
          <template #runnerCount-cell="{ row }">
            <UBadge :label="String(row.original.runnerCount)" :color="row.original.runnerCount ? 'primary' : 'neutral'" variant="subtle" />
          </template>
          <template #actions-cell="{ row }">
            <div class="flex justify-end gap-1" @click.stop>
              <UButton :to="`/repositories/${row.original.id}`" icon="i-lucide-file-search" label="Runner files" size="xs" color="neutral" variant="outline" />
              <UButton v-if="org.isAdmin" icon="i-lucide-trash-2" size="xs" color="error" variant="ghost" aria-label="Remove repository" @click="remove(row.original)" />
            </div>
          </template>
        </DataList>
      </UCard>
      <AddRepositoryModal v-model:open="addOpen" @created="r => router.push(`/repositories/${r.id}`)" />
    </template>
  </UDashboardPanel>
</template>
