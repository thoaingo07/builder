<script setup lang="ts">
import NavAction from '@/components/NavAction.vue'
import { computed, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import type { TabsItem } from '@nuxt/ui'
import { api, ApiError } from '@/api/client'
import type { PipelineDto, PlanPreviewDto, TriggerSpec } from '@/api/types'
import * as tf from '@/lib/taskfile'
import { shortSha } from '@/lib/format'
import { repoFileUrl } from '@/lib/repoLinks'
import { useNotify } from '@/composables/useNotify'
import { useCopy } from '@/composables/useClipboard'
import CodeEditor from '@/components/CodeEditor.vue'
import TaskGraph from '@/components/graph/TaskGraph.vue'
import TaskDetails from '@/components/pipelines/TaskDetails.vue'
import TriggersView from '@/components/pipelines/TriggersView.vue'
import RunPipelineModal from '@/components/pipelines/RunPipelineModal.vue'
import { useIsMobile } from '@/composables/useIsMobile'

// Read-only view of a runner file. Builder never writes to repositories: files are changed there.
const props = defineProps<{ id: string }>()

const notify = useNotify()
const copy = useCopy()

const pipeline = ref<PipelineDto | null>(null)
const branches = ref<string[]>([])
const branch = ref('')
const loadedBranch = ref('')
const commit = ref<string | null>(null)
const content = ref('')
const missingFile = ref(false)
const loading = ref(true)
const loadError = ref<string | null>(null)
const runOpen = ref(false)

const mobile = useIsMobile()
// phones: a task list instead of a zoomed-out graph
const tab = ref<'visual' | 'list' | 'yaml'>(mobile.value ? 'list' : 'visual')
const tabs = computed<TabsItem[]>(() => [
  { label: mobile.value ? undefined : 'Graph', icon: 'i-lucide-workflow', value: 'visual', 'aria-label': 'Graph' },
  { label: mobile.value ? undefined : 'Tasks', icon: 'i-lucide-list', value: 'list', 'aria-label': 'Tasks' },
  { label: mobile.value ? undefined : 'YAML', icon: 'i-lucide-file-code', value: 'yaml', 'aria-label': 'YAML' },
])
const selected = ref<string | null>(null)
const aside = ref<HTMLElement | null>(null)
// phones: the details sit below the list/graph, so bring them into view
watch(selected, async n => {
  if (!n || !mobile.value) return
  await nextTick()
  aside.value?.scrollIntoView({ block: 'start', behavior: 'smooth' })
})

// ---------- parsed model ----------
const EMPTY: tf.TaskfileModel = { version: '3', entry: '', triggers: { push: null, pullRequest: null, schedules: [], error: null }, tasks: [] }
const doc = shallowRef(tf.parse(''))
watch(content, c => { doc.value = tf.parse(c) }, { immediate: true })
const parseErrors = computed(() => tf.errorsOf(doc.value))
const model = computed<tf.TaskfileModel>(() => (parseErrors.value.length ? EMPTY : tf.read(doc.value)))
const effectiveEntry = computed(() => pipeline.value?.entryTask || model.value.entry || 'default')
const selectedTask = computed(() => model.value.tasks.find(t => t.name === selected.value) ?? null)
const reachable = computed(() => {
  if (plan.value && !plan.value.error) return new Set(plan.value.jobs.map(j => j.taskName))
  return tf.reachable(model.value, effectiveEntry.value)
})

/** the file's triggers in the API's shape, for the shared triggers view */
const triggerSpec = computed<TriggerSpec>(() => {
  const t = model.value.triggers
  const vars = (kv: tf.KeyValue[]) => Object.fromEntries(kv.map(v => [v.key, v.value]))
  return {
    push: t.push && { branches: t.push.branches, paths: t.push.paths, vars: vars(t.push.vars) },
    pullRequest: t.pullRequest && { branches: t.pullRequest.branches, paths: t.pullRequest.paths, vars: vars(t.pullRequest.vars) },
    schedules: t.schedules.map(s => ({ cron: s.cron, branch: s.branch, timeZone: s.timeZone, vars: vars(s.vars) })),
  }
})

const repoLink = computed(() => pipeline.value ? repoFileUrl(pipeline.value.repositoryUrl, pipeline.value.taskfilePath, loadedBranch.value || pipeline.value.defaultBranch) : null)

// ---------- server validation (plan preview; nothing is written) ----------
const plan = ref<PlanPreviewDto | null>(null)
const planning = ref(false)
async function validate() {
  plan.value = null
  if (parseErrors.value.length || !content.value.trim()) return
  planning.value = true
  try { plan.value = await api.pipelines.plan(content.value, effectiveEntry.value) } catch (e) { notify.error(e, 'Validation failed') } finally { planning.value = false }
}
const status = computed(() => {
  if (parseErrors.value.length) return { color: 'error' as const, icon: 'i-lucide-circle-x', text: 'YAML error' }
  if (planning.value) return { color: 'neutral' as const, icon: 'i-lucide-loader-circle', text: 'Validating…' }
  if (plan.value?.error) return { color: 'error' as const, icon: 'i-lucide-circle-x', text: 'Invalid runner file' }
  if (plan.value) return { color: 'success' as const, icon: 'i-lucide-circle-check', text: `${plan.value.jobs.length} jobs from “${plan.value.entryTask}”` }
  return { color: 'neutral' as const, icon: 'i-lucide-circle-dashed', text: 'Not validated' }
})

// ---------- load ----------
async function load(b?: string) {
  loading.value = true
  loadError.value = null
  missingFile.value = false
  try {
    const dto = await api.pipelines.taskfile(props.id, b || undefined)
    branch.value = loadedBranch.value = dto.branch
    commit.value = dto.commit
    content.value = dto.content
  } catch (e) {
    content.value = ''
    commit.value = null
    if (e instanceof ApiError && e.status === 404) {
      missingFile.value = true
      loadedBranch.value = branch.value = b || pipeline.value?.defaultBranch || 'main'
    } else {
      loadError.value = e instanceof ApiError ? (e.problem.detail ?? e.message) : (e as Error).message
    }
  } finally {
    loading.value = false
    if (selected.value && !model.value.tasks.some(t => t.name === selected.value)) selected.value = null
    void validate()
  }
}

let ready = false
watch(branch, b => { if (ready && b && b !== loadedBranch.value) void load(b) })

onMounted(async () => {
  try {
    pipeline.value = await api.pipelines.get(props.id)
  } catch (e) {
    loadError.value = e instanceof ApiError ? (e.problem.title ?? e.message) : (e as Error).message
    loading.value = false
    return
  }
  void api.repositories.branches(pipeline.value.repositoryId).then(r => { branches.value = r }).catch(() => undefined)
  await load(pipeline.value.defaultBranch)
  ready = true
})
</script>

<template>
  <UDashboardPanel id="runner-file" :ui="{ body: 'p-0 sm:p-0 gap-0 max-lg:overflow-y-auto lg:overflow-hidden' }">
    <template #header>
      <UDashboardNavbar :title="pipeline ? `${pipeline.name} · runner file` : 'Runner file'">
        <template #leading>
          <UButton icon="i-lucide-arrow-left" color="neutral" variant="ghost" to="/pipelines" aria-label="Back to runners" />
        </template>
        <template #right>
          <NavAction v-if="repoLink" :to="repoLink" target="_blank" icon="i-lucide-external-link" label="Open in repository" color="neutral" variant="outline" />
          <NavAction v-if="pipeline" icon="i-lucide-play" label="Run" @click="runOpen = true" />
        </template>
      </UDashboardNavbar>
      <UDashboardToolbar>
        <template #left>
          <USelectMenu
            v-if="branches.length" v-model="branch" :items="branches" icon="i-lucide-git-branch" size="sm" class="w-36 sm:w-48"
            :create-item="{ position: 'bottom' }" @create="(b: string) => { branches.push(b); branch = b }"
          />
          <UInput v-else v-model.lazy="branch" icon="i-lucide-git-branch" size="sm" class="w-36 sm:w-48" @keydown.enter="load(branch)" />
          <UButton icon="i-lucide-refresh-cw" size="sm" color="neutral" variant="ghost" aria-label="Reload from git" :loading="loading" @click="load(branch)" />
          <span v-if="commit" class="hidden font-mono text-xs text-muted sm:inline">{{ shortSha(commit) }}</span>
          <span class="hidden font-mono text-xs text-dimmed md:inline">{{ pipeline?.repositoryName }} · {{ pipeline?.taskfilePath }}</span>
        </template>
        <template #right>
          <UTooltip :text="plan?.error ?? parseErrors[0] ?? status.text">
            <UBadge :color="status.color" variant="subtle" :icon="status.icon" :label="mobile ? undefined : status.text" class="max-w-72 truncate" :ui="{ leadingIcon: planning ? 'animate-spin' : '' }" />
          </UTooltip>
          <UTabs v-model="tab" :items="tabs" :content="false" size="xs" variant="pill" />
        </template>
      </UDashboardToolbar>
    </template>

    <template #body>
      <UAlert
        v-if="pipeline" color="info" variant="subtle" icon="i-lucide-info" class="rounded-none"
        title="Runner files are changed in the repository"
      >
        <template #description>
          <code>{{ pipeline.taskfilePath }}</code> on <code>{{ loadedBranch || pipeline.defaultBranch }}</code>. Builder reads them; it never writes to your code.
          <a v-if="repoLink" :href="repoLink" target="_blank" rel="noopener" class="ml-1 font-medium underline">Open in repository</a>
        </template>
      </UAlert>

      <div v-if="loading && !content && !missingFile" class="p-6"><USkeleton class="h-96 w-full" /></div>
      <div v-else-if="loadError" class="p-6">
        <UEmpty icon="i-lucide-circle-alert" title="Could not load the runner file" :description="loadError" :actions="[{ label: 'Retry', icon: 'i-lucide-refresh-cw', onClick: () => load(branch) }]" />
      </div>
      <div v-else-if="missingFile" class="p-6">
        <UEmpty
          icon="i-lucide-file-x-2" :title="`${pipeline?.taskfilePath} does not exist on ${loadedBranch}`"
          description="The runner is mapped to this file, but this branch doesn't contain it. Pick another branch, or add the file in the repository."
          :actions="loadedBranch !== pipeline?.defaultBranch ? [{ label: `Show ${pipeline?.defaultBranch}`, icon: 'i-lucide-git-branch', color: 'neutral', variant: 'outline', onClick: () => { branch = pipeline!.defaultBranch } }] : []"
        />
      </div>
      <template v-else>
        <UAlert v-if="plan?.error" color="error" variant="subtle" icon="i-lucide-circle-x" class="rounded-none" title="Runner file is invalid" :description="plan.error" />
        <UAlert v-if="parseErrors.length" color="error" variant="subtle" icon="i-lucide-file-x" class="rounded-none" title="YAML syntax error" :description="parseErrors[0]" />

        <div v-show="tab !== 'yaml'" class="flex min-h-0 flex-1 flex-col lg:flex-row">
          <ul v-if="tab === 'list'" class="divide-y divide-default lg:flex-1 lg:overflow-y-auto">
            <li v-for="t in model.tasks" :key="t.name">
              <button
                type="button" class="flex w-full items-center gap-2 px-4 py-3 text-left hover:bg-elevated/50"
                :class="[selected === t.name ? 'bg-elevated' : '', reachable.has(t.name) ? '' : 'opacity-50']"
                @click="selected = selected === t.name ? null : t.name"
              >
                <UIcon v-if="t.name === effectiveEntry" name="i-lucide-flag" class="shrink-0 text-primary" />
                <span class="min-w-0 flex-1">
                  <span class="block truncate font-mono text-sm font-medium">{{ t.name }}</span>
                  <span class="block truncate text-xs text-muted">
                    {{ t.steps.length }} step{{ t.steps.length === 1 ? '' : 's' }}<template v-if="t.deps.length"> · after {{ t.deps.join(', ') }}</template>
                  </span>
                </span>
                <UIcon v-if="t.requires.length" name="i-lucide-text-cursor-input" class="text-info" />
                <UIcon v-if="t.approval" name="i-lucide-lock" class="text-warning" />
                <UIcon v-if="t.deploy" name="i-lucide-rocket" class="text-primary" />
                <UIcon name="i-lucide-chevron-right" class="text-dimmed" />
              </button>
            </li>
          </ul>
          <div v-else class="relative h-[60vh] min-h-80 flex-1 lg:h-auto lg:min-h-[420px]">
            <div v-if="!model.tasks.length" class="absolute inset-0 flex items-center justify-center">
              <UEmpty icon="i-lucide-workflow" title="No tasks" description="This file defines no tasks." variant="naked" />
            </div>
            <TaskGraph v-else :model="model" :entry="effectiveEntry" :reachable="reachable" :selected="selected" @select="n => (selected = n)" />
          </div>
          <aside ref="aside" class="w-full border-t border-default p-4 lg:w-96 lg:overflow-y-auto lg:border-t-0 lg:border-l">
            <template v-if="selectedTask">
              <UButton icon="i-lucide-arrow-left" label="Runner overview" size="xs" color="neutral" variant="link" class="mb-3 px-0" @click="selected = null" />
              <TaskDetails :task="selectedTask" :is-entry="selectedTask.name === effectiveEntry" :reachable="reachable.has(selectedTask.name)" @select="n => (selected = n)" />
            </template>
            <div v-else class="space-y-5">
              <div>
                <h3 class="font-semibold text-highlighted">Overview</h3>
                <dl class="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
                  <dt class="text-muted">Entry task</dt>
                  <dd class="font-mono">{{ effectiveEntry }} <span v-if="pipeline?.entryTask" class="font-sans text-muted">(runner setting)</span></dd>
                  <dt class="text-muted">Tasks</dt>
                  <dd>{{ model.tasks.length }} · {{ reachable.size }} run by a build</dd>
                </dl>
                <p class="mt-2 text-xs text-muted">Pick a task to see its steps, variables, inputs and Builder settings.</p>
              </div>
              <div class="space-y-2">
                <h3 class="flex items-center gap-2 font-semibold text-highlighted"><UIcon name="i-lucide-zap" class="text-muted" />Triggers <code class="text-xs font-normal text-muted">x-builder.triggers</code></h3>
                <UAlert v-if="model.triggers.error" color="error" variant="subtle" icon="i-lucide-circle-x" :title="model.triggers.error" />
                <TriggersView v-else :spec="triggerSpec" :default-branch="pipeline?.defaultBranch ?? 'main'" />
              </div>
            </div>
          </aside>
        </div>

        <div v-show="tab === 'yaml'" class="relative min-h-[420px] flex-1">
          <UButton
            icon="i-lucide-copy" label="Copy" size="xs" color="neutral" variant="outline" class="absolute top-2 right-4 z-10 bg-default"
            @click="copy(content, 'Runner file copied')"
          />
          <CodeEditor :model-value="content" readonly />
        </div>
      </template>

      <RunPipelineModal v-model:open="runOpen" :pipeline="pipeline" />
    </template>
  </UDashboardPanel>
</template>
