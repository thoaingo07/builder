<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { FormError } from '@nuxt/ui'
import { api } from '@/api/client'
import type { ConnectionDto, PipelineDto, PipelineInput, RepositoryDto } from '@/api/types'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ pipeline: PipelineDto | null }>()
const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ saved: [PipelineDto] }>()

const notify = useNotify()
const NONE = 'none'

const state = reactive({ name: '', connectionId: NONE, repositoryUrl: '', defaultBranch: 'main', taskfilePath: 'Taskfile.yml', entryTask: '' })
const connections = ref<ConnectionDto[]>([])
const repos = ref<RepositoryDto[]>([])
const reposLoading = ref(false)
const pickedRepo = ref<RepositoryDto & { label: string } | undefined>()
const saving = ref(false)

const connection = computed(() => connections.value.find(c => c.id === state.connectionId) ?? null)
const connectionItems = computed(() => [
  { label: 'None (public / URL with credentials)', value: NONE },
  ...connections.value.map(c => ({ label: `${c.name} · ${c.type === 'AzureDevOps' ? 'Azure DevOps' : 'Git'}`, value: c.id })),
])
const repoItems = computed(() => repos.value.map(r => ({ ...r, label: `${r.project} / ${r.name}` })))

watch(open, async isOpen => {
  if (!isOpen) return
  const p = props.pipeline
  Object.assign(state, {
    name: p?.name ?? '', connectionId: p?.connectionId ?? NONE, repositoryUrl: p?.repositoryUrl ?? '',
    defaultBranch: p?.defaultBranch ?? 'main', taskfilePath: p?.taskfilePath ?? 'Taskfile.yml', entryTask: p?.entryTask ?? '',
  })
  pickedRepo.value = undefined
  try { connections.value = await api.connections.list() } catch (e) { notify.error(e, 'Could not load connections') }
}, { immediate: true })

watch(() => state.connectionId, async () => {
  repos.value = []
  if (connection.value?.type !== 'AzureDevOps') return
  reposLoading.value = true
  try { repos.value = await api.connections.repositories(connection.value.id) } catch (e) { notify.error(e, 'Could not list repositories') } finally { reposLoading.value = false }
})

watch(pickedRepo, r => {
  if (!r) return
  state.repositoryUrl = r.url
  if (r.defaultBranch) state.defaultBranch = r.defaultBranch.replace(/^refs\/heads\//, '')
  if (!state.name) state.name = r.name
})

function validate(s: typeof state): FormError[] {
  const errors: FormError[] = []
  if (!s.name.trim()) errors.push({ name: 'name', message: 'Required' })
  if (!s.repositoryUrl.trim()) errors.push({ name: 'repositoryUrl', message: 'Required' })
  if (!s.taskfilePath.trim()) errors.push({ name: 'taskfilePath', message: 'Required' })
  return errors
}

async function submit() {
  saving.value = true
  const input: PipelineInput = {
    name: state.name.trim(), connectionId: state.connectionId === NONE ? null : state.connectionId,
    repositoryUrl: state.repositoryUrl.trim(), defaultBranch: state.defaultBranch.trim() || 'main',
    taskfilePath: state.taskfilePath.trim(), entryTask: state.entryTask.trim() || null,
  }
  try {
    const saved = props.pipeline ? await api.pipelines.update(props.pipeline.id, input) : await api.pipelines.create(input)
    notify.success(props.pipeline ? 'Pipeline updated' : 'Pipeline created')
    emit('saved', saved)
    open.value = false
  } catch (e) {
    notify.error(e, 'Could not save pipeline')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" :title="pipeline ? 'Edit pipeline' : 'New pipeline'" description="A pipeline is a Taskfile in a git repository." :ui="{ content: 'sm:max-w-xl' }">
    <template #body>
      <UForm id="pipeline-form" :state="state" :validate="validate" class="grid gap-4 sm:grid-cols-2" @submit="submit">
        <UFormField label="Name" name="name" required class="sm:col-span-2">
          <UInput v-model="state.name" class="w-full" placeholder="shop-api" />
        </UFormField>
        <UFormField label="Connection" name="connectionId" class="sm:col-span-2" help="Credentials used to clone and to commit Taskfile edits.">
          <USelect v-model="state.connectionId" :items="connectionItems" class="w-full" />
        </UFormField>
        <UFormField v-if="connection?.type === 'AzureDevOps'" label="Repository" class="sm:col-span-2">
          <USelectMenu
            v-model="pickedRepo" :items="repoItems" :loading="reposLoading" placeholder="Pick an Azure DevOps repository"
            icon="i-lucide-folder-git-2" class="w-full"
          />
        </UFormField>
        <UFormField label="Repository URL" name="repositoryUrl" required class="sm:col-span-2">
          <UInput v-model="state.repositoryUrl" class="w-full font-mono" placeholder="https://dev.azure.com/org/project/_git/repo" />
        </UFormField>
        <UFormField label="Default branch" name="defaultBranch">
          <UInput v-model="state.defaultBranch" class="w-full" icon="i-lucide-git-branch" />
        </UFormField>
        <UFormField label="Taskfile path" name="taskfilePath" required>
          <UInput v-model="state.taskfilePath" class="w-full font-mono" />
        </UFormField>
        <UFormField label="Entry task" name="entryTask" help="Empty = x-builder.entry, then 'default'." class="sm:col-span-2">
          <UInput v-model="state.entryTask" class="w-full font-mono" placeholder="ci" />
        </UFormField>
      </UForm>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="pipeline-form" :loading="saving" :label="pipeline ? 'Save' : 'Create'" />
      </div>
    </template>
  </UModal>
</template>
