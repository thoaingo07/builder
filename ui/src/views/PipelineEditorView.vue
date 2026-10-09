<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import type { Document } from 'yaml'
import type { TabsItem } from '@nuxt/ui'
import { api, ApiError } from '@/api/client'
import type { EnvironmentDto, PipelineDto, PlanPreviewDto } from '@/api/types'
import * as tf from '@/lib/taskfile'
import { debounce } from '@/lib/collections'
import { shortSha } from '@/lib/format'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import CodeEditor from '@/components/CodeEditor.vue'
import TaskGraphEditor from '@/components/graph/TaskGraphEditor.vue'
import TaskForm from '@/components/pipelines/TaskForm.vue'

const props = defineProps<{ id: string }>()

const notify = useNotify()
const confirm = useConfirm()

const pipeline = ref<PipelineDto | null>(null)
const environments = ref<EnvironmentDto[]>([])
const secretNames = ref<string[]>([])
const branch = ref('')
const loadedBranch = ref('')
const commit = ref<string | null>(null)
const content = ref('')
const baseline = ref('')
const missingFile = ref(false)
const loading = ref(true)
const loadError = ref<string | null>(null)

const tab = ref<'visual' | 'yaml'>('visual')
const tabs: TabsItem[] = [
  { label: 'Visual', icon: 'i-lucide-workflow', value: 'visual' },
  { label: 'YAML', icon: 'i-lucide-file-code', value: 'yaml' },
]

const selected = ref<string | null>(null)
const graph = ref<InstanceType<typeof TaskGraphEditor> | null>(null)

// ---------- parsed model ----------
const doc = shallowRef<Document>(tf.parse(''))
watch(content, c => { doc.value = tf.parse(c) }, { immediate: true })
const parseErrors = computed(() => tf.errorsOf(doc.value))
const lastGood = shallowRef<tf.TaskfileModel>({ version: '3', entry: '', tasks: [] })
const model = computed<tf.TaskfileModel>(() => {
  if (parseErrors.value.length) return lastGood.value
  const m = tf.read(doc.value)
  lastGood.value = m
  return m
})
const effectiveEntry = computed(() => pipeline.value?.entryTask || model.value.entry || 'default')
const taskNames = computed(() => model.value.tasks.map(t => t.name))
const selectedTask = computed(() => model.value.tasks.find(t => t.name === selected.value) ?? null)
const dirty = computed(() => content.value !== baseline.value)

// ---------- server validation ----------
const plan = ref<PlanPreviewDto | null>(null)
const planning = ref(false)
const validate = debounce(async () => {
  if (parseErrors.value.length || !content.value.trim()) { plan.value = null; return }
  planning.value = true
  try { plan.value = await api.pipelines.plan(content.value, effectiveEntry.value) } catch (e) { notify.error(e, 'Validation failed') } finally { planning.value = false }
}, 700)
watch([content, effectiveEntry], () => validate())

const reachable = computed(() => {
  if (plan.value && !plan.value.error) return new Set(plan.value.jobs.map(j => j.taskName))
  return tf.reachable(model.value, effectiveEntry.value)
})
const status = computed(() => {
  if (parseErrors.value.length) return { color: 'error' as const, icon: 'i-lucide-circle-x', text: 'YAML error' }
  if (planning.value) return { color: 'neutral' as const, icon: 'i-lucide-loader-circle', text: 'Validating…' }
  if (plan.value?.error) return { color: 'error' as const, icon: 'i-lucide-circle-x', text: 'Invalid runner file' }
  if (plan.value) return { color: 'success' as const, icon: 'i-lucide-circle-check', text: `${plan.value.jobs.length} jobs from “${plan.value.entryTask}”` }
  return { color: 'neutral' as const, icon: 'i-lucide-circle-dashed', text: 'Not validated' }
})

// ---------- edits ----------
function mutate(fn: (d: Document) => void) {
  if (parseErrors.value.length) { notify.error(new Error('Fix the YAML errors first.'), 'Cannot edit visually'); return }
  const d = tf.parse(content.value)
  try {
    fn(d)
    content.value = tf.toYaml(d)
  } catch (e) {
    notify.error(e, 'Edit failed')
  }
}

const newTaskOpen = ref(false)
const newTaskName = ref('')
const newTaskError = computed(() => {
  const n = newTaskName.value.trim()
  if (!n) return null
  if (!tf.TASK_NAME.test(n)) return 'Letters, digits, _ . : - only'
  if (taskNames.value.includes(n)) return 'Already exists'
  return null
})
function addTask() {
  const n = newTaskName.value.trim()
  if (!n || newTaskError.value) return
  mutate(d => tf.addTask(d, n, selected.value ? [selected.value] : []))
  selected.value = n
  newTaskName.value = ''
  newTaskOpen.value = false
}

function rename(from: string, to: string) {
  mutate(d => tf.renameTask(d, from, to))
  selected.value = to
}

async function removeTask(name: string) {
  if (!await confirm({ title: 'Delete task', message: `Delete "${name}"? It is also removed from other tasks' deps.`, confirmLabel: 'Delete', danger: true })) return
  mutate(d => tf.deleteTask(d, name))
  selected.value = null
}

const connect = (dep: string, task: string) => mutate(d => tf.addDep(d, task, dep))
const disconnect = (dep: string, task: string) => mutate(d => tf.removeDep(d, task, dep))
const makeEntry = (name: string) => mutate(d => tf.setEntry(d, name))

const entryItems = computed(() => taskNames.value.map(n => ({ label: n, value: n })))
const entryModel = computed({
  get: () => model.value.entry || undefined,
  set: (v: string | undefined) => mutate(d => tf.setEntry(d, v ?? '')),
})

// ---------- load / save ----------
async function load(b?: string) {
  loading.value = true
  loadError.value = null
  missingFile.value = false
  try {
    const tfDto = await api.pipelines.taskfile(props.id, b || undefined)
    branch.value = loadedBranch.value = tfDto.branch
    commit.value = tfDto.commit
    content.value = baseline.value = tfDto.content
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) {
      missingFile.value = true
      loadedBranch.value = branch.value = b || pipeline.value?.defaultBranch || 'main'
      commit.value = null
      // don't silently fill in a template: the user decides (see the empty state)
      content.value = baseline.value = ''
    } else {
      loadError.value = e instanceof ApiError ? (e.problem.detail ?? e.message) : (e as Error).message
    }
  } finally {
    loading.value = false
    selected.value = null
    validate()
  }
}

function startFromTemplate() {
  content.value = tf.STARTER
}

async function reload() {
  if (dirty.value && !await confirm({ title: 'Discard changes', message: 'Reloading discards your unsaved edits.', confirmLabel: 'Discard', danger: true })) return
  await load(branch.value.trim())
}

const saveOpen = ref(false)
const saveMessage = ref('')
const saving = ref(false)
function openSave() {
  saveMessage.value = `Update ${pipeline.value?.taskfilePath ?? 'Taskfile.yml'} via Builder`
  saveOpen.value = true
}
async function save() {
  if (!saveMessage.value.trim()) return
  saving.value = true
  try {
    const res = await api.pipelines.saveTaskfile(props.id, loadedBranch.value, content.value, saveMessage.value.trim())
    commit.value = res.commit
    baseline.value = content.value
    missingFile.value = false
    saveOpen.value = false
    notify.success('Taskfile committed', `${shortSha(res.commit)} on ${res.branch}`)
  } catch (e) {
    notify.error(e, 'Commit failed')
  } finally {
    saving.value = false
  }
}

onBeforeRouteLeave(async () => {
  if (!dirty.value) return true
  return await confirm({ title: 'Unsaved changes', message: 'Leave the editor and discard your changes?', confirmLabel: 'Leave', danger: true })
})
function beforeUnload(e: BeforeUnloadEvent) { if (dirty.value) e.preventDefault() }

onMounted(async () => {
  window.addEventListener('beforeunload', beforeUnload)
  try {
    pipeline.value = await api.pipelines.get(props.id)
  } catch (e) {
    loadError.value = e instanceof ApiError ? (e.problem.title ?? e.message) : (e as Error).message
    loading.value = false
    return
  }
  void api.environments.list().then(r => { environments.value = r }).catch(() => undefined)
  void api.secrets.list().then(r => { secretNames.value = r.map(x => x.name) }).catch(() => undefined)
  await load(pipeline.value.defaultBranch)
})
onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
</script>

<template>
  <UDashboardPanel id="pipeline-editor" :ui="{ body: 'p-0 sm:p-0 gap-0 overflow-hidden' }">
    <template #header>
      <UDashboardNavbar :title="pipeline ? `${pipeline.name} · runner file` : 'Runner file'">
        <template #leading>
          <UButton icon="i-lucide-arrow-left" color="neutral" variant="ghost" to="/pipelines" aria-label="Back to runners" />
        </template>
        <template #trailing>
          <UBadge v-if="dirty" color="warning" variant="subtle" label="Unsaved" size="sm" />
        </template>
        <template #right>
          <UButton icon="i-lucide-git-commit-horizontal" label="Commit" :disabled="!dirty || parseErrors.length > 0" @click="openSave" />
        </template>
      </UDashboardNavbar>
      <UDashboardToolbar>
        <template #left>
          <UInput v-model="branch" icon="i-lucide-git-branch" size="sm" class="w-44" @keydown.enter="reload" />
          <UButton icon="i-lucide-refresh-cw" size="sm" color="neutral" variant="ghost" aria-label="Reload from git" :loading="loading" @click="reload" />
          <span v-if="commit" class="font-mono text-xs text-muted">{{ shortSha(commit) }}</span>
          <span class="hidden font-mono text-xs text-dimmed md:inline">{{ pipeline?.repositoryName }} · {{ pipeline?.taskfilePath }}</span>
        </template>
        <template #right>
          <UTooltip :text="plan?.error ?? parseErrors[0] ?? status.text">
            <UBadge :color="status.color" variant="subtle" :icon="status.icon" :label="status.text" class="max-w-72 truncate" :ui="{ leadingIcon: planning ? 'animate-spin' : '' }" />
          </UTooltip>
          <UTabs v-model="tab" :items="tabs" :content="false" size="xs" variant="pill" />
        </template>
      </UDashboardToolbar>
    </template>

    <template #body>
      <div v-if="loading && !content" class="p-6"><USkeleton class="h-96 w-full" /></div>
      <div v-else-if="loadError" class="p-6">
        <UEmpty icon="i-lucide-circle-alert" title="Could not load the Taskfile" :description="loadError" :actions="[{ label: 'Retry', icon: 'i-lucide-refresh-cw', onClick: () => load(branch) }]" />
      </div>
      <div v-else-if="missingFile && !content" class="p-6">
        <UEmpty
          icon="i-lucide-file-x-2" :title="`${pipeline?.taskfilePath} does not exist on ${loadedBranch}`"
          description="The runner is mapped to this file, but the branch doesn't contain it. Pick another branch above, or create the file from a starter template and commit it to this branch."
          :actions="[
            { label: 'Create from starter template', icon: 'i-lucide-file-plus', onClick: startFromTemplate },
            { label: 'Default branch', icon: 'i-lucide-git-branch', color: 'neutral', variant: 'outline', onClick: () => load(pipeline?.defaultBranch) },
          ]"
        />
      </div>
      <template v-else>
        <UAlert
          v-if="missingFile" color="info" variant="subtle" icon="i-lucide-file-plus" class="rounded-none"
          :title="`New file: ${pipeline?.taskfilePath} on ${loadedBranch}`" description="Started from the starter template — commit to create it in the repository."
        />
        <UAlert
          v-if="plan?.error" color="error" variant="subtle" icon="i-lucide-circle-x" class="rounded-none" title="Runner file is invalid" :description="plan.error"
        />
        <UAlert
          v-if="parseErrors.length" color="error" variant="subtle" icon="i-lucide-file-x" class="rounded-none"
          title="YAML syntax error — the visual editor shows the last valid version" :description="parseErrors[0]"
        />

        <div v-show="tab === 'visual'" class="flex min-h-0 flex-1 flex-col lg:flex-row">
          <div class="relative min-h-[420px] flex-1">
            <div class="absolute top-3 left-3 z-10 flex flex-wrap items-center gap-2">
              <UPopover v-model:open="newTaskOpen">
                <UButton icon="i-lucide-plus" label="Add task" size="sm" />
                <template #content>
                  <form class="w-64 space-y-2 p-3" @submit.prevent="addTask">
                    <UFormField label="Task name" :error="newTaskError ?? undefined" :help="selected ? `Will depend on ${selected}` : undefined">
                      <UInput v-model="newTaskName" class="w-full font-mono" autofocus placeholder="build" />
                    </UFormField>
                    <UButton type="submit" block size="sm" label="Add" :disabled="!newTaskName.trim() || !!newTaskError" />
                  </form>
                </template>
              </UPopover>
              <UButton icon="i-lucide-layout-grid" label="Auto layout" size="sm" color="neutral" variant="outline" class="bg-default" @click="graph?.autoLayout()" />
              <USelectMenu
                v-model="entryModel" :items="entryItems" value-key="value" size="sm" class="w-48 bg-default"
                icon="i-lucide-flag" placeholder="x-builder.entry"
              />
            </div>
            <div v-if="!model.tasks.length" class="absolute inset-0 flex items-center justify-center">
              <UEmpty icon="i-lucide-workflow" title="No tasks" description="Add a task to start building the runner." variant="naked" />
            </div>
            <TaskGraphEditor
              ref="graph" :model="model" :entry="model.entry || effectiveEntry" :reachable="reachable" :selected="selected"
              @select="n => (selected = n)"
              @connect="connect" @disconnect="disconnect"
            />
            <UTooltip text="Drag from a task's right handle onto another task to add a dependency. Click an edge and press Delete to remove it.">
              <UButton icon="i-lucide-circle-help" color="neutral" variant="ghost" size="sm" class="absolute top-3 right-3 z-10" aria-label="Graph editing help" />
            </UTooltip>
          </div>
          <aside class="max-h-[60vh] w-full overflow-y-auto border-t border-default p-4 lg:max-h-none lg:w-96 lg:border-t-0 lg:border-l">
            <TaskForm
              v-if="selectedTask" :task="selectedTask" :task-names="taskNames" :environments="environments" :secret-names="secretNames"
              :is-entry="model.entry === selectedTask.name"
              @mutate="mutate" @rename="rename" @remove="removeTask" @set-entry="makeEntry"
            />
            <UEmpty
              v-else icon="i-lucide-mouse-pointer-click" title="Select a task" variant="naked"
              description="Click a task in the graph to edit its commands, dependencies, approval and deployment."
            />
          </aside>
        </div>

        <div v-show="tab === 'yaml'" class="min-h-[420px] flex-1">
          <CodeEditor v-model="content" />
        </div>
      </template>

      <UModal v-model:open="saveOpen" title="Commit Taskfile" :description="`Pushes ${pipeline?.taskfilePath} to ${loadedBranch}.`">
        <template #body>
          <UFormField label="Commit message" required>
            <UTextarea v-model="saveMessage" :rows="3" autoresize class="w-full" autofocus />
          </UFormField>
        </template>
        <template #footer>
          <div class="flex w-full justify-end gap-2">
            <UButton color="neutral" variant="outline" label="Cancel" @click="saveOpen = false" />
            <UButton icon="i-lucide-git-commit-horizontal" label="Commit & push" :loading="saving" :disabled="!saveMessage.trim()" @click="save" />
          </div>
        </template>
      </UModal>
    </template>
  </UDashboardPanel>
</template>
