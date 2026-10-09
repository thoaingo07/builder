<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api } from '@/api/client'
import type { ConnectionDto, RemoteRepositoryDto, RepositoryDto } from '@/api/types'
import { useNotify } from '@/composables/useNotify'

const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ created: [RepositoryDto] }>()
const notify = useNotify()

const NONE = 'none'
const connections = ref<ConnectionDto[]>([])
const s = reactive({ connectionId: NONE, project: '', url: '', name: '', defaultBranch: '' })
const projects = ref<string[]>([])
const remoteRepos = ref<RemoteRepositoryDto[]>([])
const pickedRepo = ref<string | undefined>()
const branches = ref<string[]>([])
const loading = reactive({ projects: false, repos: false, branches: false })
const branchError = ref<string | null>(null)
const saving = ref(false)

const connection = computed(() => connections.value.find(c => c.id === s.connectionId) ?? null)
const isAzure = computed(() => connection.value?.type === 'AzureDevOps')
const connectionItems = computed(() => [
  ...connections.value.map(c => ({ label: c.name, value: c.id, icon: c.type === 'AzureDevOps' ? 'i-simple-icons-azuredevops' : 'i-lucide-git-fork' })),
  { label: 'None — public repository', value: NONE, icon: 'i-lucide-globe' },
])
const repoItems = computed(() => remoteRepos.value.map(r => ({ label: r.name, value: r.url })))
const branchItems = computed(() => branches.value)
const valid = computed(() => /^https?:\/\//i.test(s.url.trim()) || /^git@/.test(s.url.trim()))

watch(open, async o => {
  if (!o) return
  Object.assign(s, { connectionId: NONE, project: '', url: '', name: '', defaultBranch: '' })
  projects.value = []; remoteRepos.value = []; branches.value = []; pickedRepo.value = undefined; branchError.value = null
  try {
    // Azure (ARM) connections only issue registry/cloud tokens; they can't read git
    connections.value = (await api.connections.list()).filter(c => c.type !== 'Azure')
    const first = connections.value.find(c => c.type === 'AzureDevOps') ?? connections.value[0]
    if (first) s.connectionId = first.id
  } catch (e) { notify.error(e, 'Could not load connections') }
})

watch(() => s.connectionId, async () => {
  s.project = ''; projects.value = []; remoteRepos.value = []; pickedRepo.value = undefined
  if (!isAzure.value || !connection.value) return
  loading.projects = true
  try {
    projects.value = await api.connections.projects(connection.value.id)
    if (projects.value.length === 1) s.project = projects.value[0]
  } catch (e) { notify.error(e, 'Could not list Azure DevOps projects') } finally { loading.projects = false }
})

watch(() => s.project, async p => {
  remoteRepos.value = []; pickedRepo.value = undefined
  if (!p || !connection.value) return
  loading.repos = true
  try { remoteRepos.value = await api.connections.repositories(connection.value.id, p) } catch (e) { notify.error(e, 'Could not list repositories') } finally { loading.repos = false }
})

watch(pickedRepo, url => {
  const r = remoteRepos.value.find(x => x.url === url)
  if (!r) return
  s.url = r.url
  s.name = r.name
  s.defaultBranch = (r.defaultBranch ?? '').replace(/^refs\/heads\//, '')
})

// branches through the connection once a URL is known; free text if that fails or there is no connection
let branchSeq = 0
watch(() => [s.url, s.connectionId] as const, async ([url]) => {
  const seq = ++branchSeq
  branches.value = []; branchError.value = null
  if (!connection.value || !valid.value) return
  loading.branches = true
  try {
    const list = await api.connections.branches(connection.value.id, url.trim())
    if (seq !== branchSeq) return
    branches.value = list
    if (!s.defaultBranch || !list.includes(s.defaultBranch)) s.defaultBranch = list.find(b => b === 'main' || b === 'master') ?? list[0] ?? s.defaultBranch
  } catch (e) {
    if (seq === branchSeq) branchError.value = e instanceof Error ? e.message : 'Could not list branches'
  } finally { if (seq === branchSeq) loading.branches = false }
})

async function submit() {
  if (!valid.value) return
  saving.value = true
  try {
    const repo = await api.repositories.create({
      connectionId: s.connectionId === NONE ? null : s.connectionId,
      url: s.url.trim(), name: s.name.trim() || null, defaultBranch: s.defaultBranch.trim() || null,
    })
    notify.success(`Added ${repo.name}`)
    open.value = false
    emit('created', repo)
  } catch (e) {
    notify.error(e, 'Could not add repository')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" title="Add repository" description="Builder reads runner files from .builder/runners/ in this repository." :ui="{ content: 'sm:max-w-xl' }">
    <template #body>
      <form id="add-repo" class="space-y-4" @submit.prevent="submit">
        <UFormField label="Connection" :help="connections.length ? undefined : 'No connections yet — add one under Connections for private repositories.'">
          <USelect v-model="s.connectionId" :items="connectionItems" class="w-full" />
        </UFormField>

        <template v-if="isAzure">
          <UFormField label="Project">
            <USelectMenu v-model="s.project" :items="projects" :loading="loading.projects" class="w-full" placeholder="Select a project" icon="i-lucide-folder" />
          </UFormField>
          <UFormField label="Repository">
            <USelectMenu
              v-model="pickedRepo" :items="repoItems" value-key="value" :loading="loading.repos" :disabled="!s.project"
              class="w-full" placeholder="Select a repository" icon="i-lucide-folder-git-2"
            />
          </UFormField>
        </template>

        <UFormField label="Repository URL" required :help="isAzure ? 'Filled in from the selection above.' : 'HTTPS clone URL'">
          <UInput v-model="s.url" class="w-full font-mono" :readonly="isAzure && !!pickedRepo" placeholder="https://github.com/contoso/shop.git" />
        </UFormField>

        <div class="grid gap-4 sm:grid-cols-2">
          <UFormField label="Name" help="Defaults to the repository name">
            <UInput v-model="s.name" class="w-full" />
          </UFormField>
          <UFormField label="Default branch" :error="branchError ?? undefined">
            <USelectMenu
              v-if="branchItems.length" v-model="s.defaultBranch" :items="branchItems" :loading="loading.branches"
              class="w-full" icon="i-lucide-git-branch"
            />
            <UInput v-else v-model="s.defaultBranch" class="w-full" icon="i-lucide-git-branch" placeholder="main" :loading="loading.branches" />
          </UFormField>
        </div>
      </form>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="add-repo" label="Add repository" :loading="saving" :disabled="!valid" />
      </div>
    </template>
  </UModal>
</template>
