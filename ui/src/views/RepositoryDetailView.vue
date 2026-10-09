<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import type { TableColumn } from '@nuxt/ui'
import { api, ApiError } from '@/api/client'
import type { RepositoryDto, RunnerFileDto, RunnerFilesDto } from '@/api/types'
import { shortSha } from '@/lib/format'
import { useOrgStore } from '@/stores/org'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ id: string }>()
const org = useOrgStore()
const notify = useNotify()

const repo = ref<RepositoryDto | null>(null)
const notFound = ref(false)
const branches = ref<string[]>([])
const branch = ref('')
const files = ref<RunnerFilesDto | null>(null)
const filesError = ref<string | null>(null)
const loadingFiles = ref(false)
const mapping = ref(false)

/** per-file mapping choices, keyed by path */
const picks = reactive<Record<string, { selected: boolean; name: string; entryTask: string }>>({})

const mappable = (f: RunnerFileDto) => !f.error && !f.mappedRunnerId
const selectedCount = computed(() => Object.entries(picks).filter(([p, v]) => v.selected && files.value?.files.some(f => f.path === p && mappable(f))).length)
const allMappableSelected = computed(() => {
  const list = files.value?.files.filter(mappable) ?? []
  return list.length > 0 && list.every(f => picks[f.path]?.selected)
})

const columns: TableColumn<RunnerFileDto>[] = [
  { id: 'select', header: '' },
  { accessorKey: 'path', header: 'Runner file' },
  { id: 'name', header: 'Runner name' },
  { id: 'entry', header: 'Entry task' },
  { accessorKey: 'tasks', header: 'Tasks' },
]

async function loadFiles() {
  loadingFiles.value = true
  filesError.value = null
  try {
    files.value = await api.repositories.runnerFiles(props.id, branch.value || null)
    for (const f of files.value.files) {
      picks[f.path] = picks[f.path] ?? { selected: mappable(f), name: f.suggestedName, entryTask: f.entryTask ?? '' }
      if (!mappable(f)) picks[f.path].selected = false
    }
  } catch (e) {
    files.value = null
    filesError.value = e instanceof ApiError ? (e.problem.detail ?? e.problem.title ?? e.message) : (e as Error).message
  } finally {
    loadingFiles.value = false
  }
}

function toggleAll(v: boolean) {
  for (const f of files.value?.files ?? []) if (mappable(f)) picks[f.path].selected = v
}

async function mapSelected() {
  const chosen = (files.value?.files ?? []).filter(f => mappable(f) && picks[f.path]?.selected)
  if (!chosen.length) return
  mapping.value = true
  try {
    const created = await api.repositories.mapRunners(props.id, {
      runners: chosen.map(f => ({ path: f.path, name: picks[f.path].name.trim() || null, entryTask: picks[f.path].entryTask.trim() || null })),
    })
    notify.success(`Mapped ${created.length} runner${created.length === 1 ? '' : 's'}`, created.map(r => r.name).join(', '))
    await loadFiles()
    if (repo.value) repo.value = { ...repo.value, runnerCount: repo.value.runnerCount + created.length }
  } catch (e) {
    notify.error(e, 'Mapping failed')
  } finally {
    mapping.value = false
  }
}

let ready = false
watch(branch, b => { if (ready && b) void loadFiles() })

onMounted(async () => {
  try {
    repo.value = await api.repositories.get(props.id)
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) notFound.value = true
    else notify.error(e, 'Could not load repository')
    return
  }
  try {
    branches.value = await api.repositories.branches(props.id)
  } catch (e) {
    notify.error(e, 'Could not list branches')
  }
  branch.value = repo.value.defaultBranch || branches.value[0] || ''
  await loadFiles()
  ready = true
})
</script>

<template>
  <UDashboardPanel id="repository">
    <template #header>
      <UDashboardNavbar :title="repo?.name ?? 'Repository'">
        <template #leading>
          <UButton icon="i-lucide-arrow-left" color="neutral" variant="ghost" to="/repositories" aria-label="Back to repositories" />
        </template>
        <template #right>
          <UButton
            v-if="org.isAdmin && files?.files.length" icon="i-lucide-link-2" :label="`Map selected${selectedCount ? ` (${selectedCount})` : ''}`"
            :disabled="!selectedCount" :loading="mapping" @click="mapSelected"
          />
        </template>
      </UDashboardNavbar>
      <UDashboardToolbar v-if="repo">
        <template #left>
          <USelectMenu
            v-if="branches.length" v-model="branch" :items="branches" icon="i-lucide-git-branch" size="sm" class="w-56"
          />
          <UInput v-else v-model.lazy="branch" icon="i-lucide-git-branch" size="sm" class="w-56" @keydown.enter="loadFiles" />
          <UButton icon="i-lucide-refresh-cw" size="sm" color="neutral" variant="ghost" aria-label="Reload runner files" :loading="loadingFiles" @click="loadFiles" />
          <span v-if="files" class="font-mono text-xs text-muted">{{ shortSha(files.commit) }}</span>
        </template>
        <template #right>
          <span class="hidden max-w-md truncate font-mono text-xs text-muted md:inline">{{ repo.url }}</span>
          <UBadge :label="repo.connectionName ?? 'public'" color="neutral" variant="subtle" icon="i-lucide-plug" />
        </template>
      </UDashboardToolbar>
    </template>

    <template #body>
      <UEmpty v-if="notFound" icon="i-lucide-search-x" title="Repository not found" description="It may belong to another organization or have been removed." :actions="[{ label: 'Repositories', to: '/repositories' }]" />

      <template v-else>
        <UAlert v-if="filesError" color="error" variant="subtle" icon="i-lucide-circle-x" title="Could not read the repository" :description="filesError" />

        <div v-if="loadingFiles && !files" class="space-y-2"><USkeleton v-for="i in 3" :key="i" class="h-12 w-full" /></div>

        <UEmpty
          v-else-if="files && !files.files.length" icon="i-lucide-folder-search" :title="`No runner files on ${files.branch}`" variant="outline"
        >
          <template #description>
            <div class="max-w-lg space-y-2 text-left">
              <p>Builder looks for go-task Taskfiles in <code>.builder/runners/*.yml</code> (or <code>.yaml</code>). Each file becomes one runner you can build.</p>
              <p>Commands run from the <strong>repository root</strong>, so a runner file can use the same paths as your project. Example <code>.builder/runners/ci.yml</code>:</p>
              <pre class="overflow-x-auto rounded bg-elevated p-2 text-xs">version: '3'
x-builder:
  entry: ci
tasks:
  ci:
    deps: [test]
  test:
    cmds: [dotnet test]</pre>
            </div>
          </template>
        </UEmpty>

        <UCard v-else-if="files" :ui="{ body: 'p-0 sm:p-0', header: 'sm:px-4 px-3 py-3' }">
          <template #header>
            <div class="flex items-center gap-2">
              <h2 class="flex-1 font-semibold text-highlighted">Runner files on {{ files.branch }}</h2>
              <span class="text-xs text-muted">.builder/runners/ · run from the repository root</span>
            </div>
          </template>
          <UTable :data="files.files" :columns="columns" :loading="loadingFiles">
            <template #select-header>
              <UCheckbox v-if="org.isAdmin" :model-value="allMappableSelected" aria-label="Select all" @update:model-value="v => toggleAll(v === true)" />
            </template>
            <template #select-cell="{ row }">
              <UTooltip v-if="row.original.error" :text="row.original.error">
                <UCheckbox :model-value="false" disabled aria-label="Cannot be mapped" />
              </UTooltip>
              <UCheckbox
                v-else-if="org.isAdmin && !row.original.mappedRunnerId" v-model="picks[row.original.path].selected"
                :aria-label="`Select ${row.original.path}`"
              />
              <UIcon v-else-if="row.original.mappedRunnerId" name="i-lucide-check" class="text-success" />
            </template>
            <template #path-cell="{ row }">
              <div class="font-mono text-sm">{{ row.original.path }}</div>
              <UTooltip v-if="row.original.error" :text="row.original.error">
                <div class="flex max-w-sm items-center gap-1 truncate text-xs text-error">
                  <UIcon name="i-lucide-circle-alert" class="shrink-0" />{{ row.original.error }}
                </div>
              </UTooltip>
            </template>
            <template #name-cell="{ row }">
              <UButton
                v-if="row.original.mappedRunnerId" :to="`/pipelines/${row.original.mappedRunnerId}/editor`" size="xs" color="primary" variant="soft"
                icon="i-lucide-workflow" :label="`mapped as ${row.original.mappedRunnerName}`"
              />
              <UInput
                v-else-if="org.isAdmin && !row.original.error" v-model="picks[row.original.path].name" size="sm" class="w-44"
              />
              <span v-else class="text-sm text-muted">{{ row.original.suggestedName }}</span>
            </template>
            <template #entry-cell="{ row }">
              <UInput
                v-if="org.isAdmin && mappable(row.original)" v-model="picks[row.original.path].entryTask" size="sm" class="w-32 font-mono"
                :placeholder="row.original.entryTask ?? 'default'"
              />
              <span v-else class="font-mono text-sm text-muted">{{ row.original.entryTask ?? '—' }}</span>
            </template>
            <template #tasks-cell="{ row }">
              <div class="flex max-w-md flex-wrap gap-1">
                <UBadge v-for="t in row.original.tasks.slice(0, 8)" :key="t" :label="t" size="sm" color="neutral" variant="soft" class="font-mono" />
                <span v-if="row.original.tasks.length > 8" class="text-xs text-muted">+{{ row.original.tasks.length - 8 }}</span>
              </div>
            </template>
          </UTable>
        </UCard>
        <p v-if="files?.files.length && !org.isAdmin" class="text-xs text-muted">Only admins can map runner files to runners.</p>
      </template>
    </template>
  </UDashboardPanel>
</template>
