<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { Document } from 'yaml'
import type { EnvironmentDto } from '@/api/types'
import * as tf from '@/lib/taskfile'
import { debounce } from '@/lib/collections'

const props = defineProps<{ task: tf.TaskModel; taskNames: string[]; environments: EnvironmentDto[]; isEntry: boolean }>()
const emit = defineEmits<{
  mutate: [fn: (doc: Document) => void]
  rename: [from: string, to: string]
  remove: [name: string]
  setEntry: [name: string]
}>()

// Local copy so typing isn't interrupted by the YAML round-trip; re-synced when another task is selected.
const s = reactive({
  name: '', desc: '', deps: [] as string[], cmds: [] as string[], labels: [] as string[], artifacts: [] as string[],
  approval: false, approvalMessage: '', approvers: [] as string[],
  deploy: false, deployModel: { environment: '', compose: '', project: '', manifests: '', namespace: '', url: '' } as tf.DeployModel,
})
const nameError = ref<string | null>(null)

function sync(t: tf.TaskModel) {
  Object.assign(s, {
    name: t.name, desc: t.desc, deps: [...t.deps], cmds: t.cmds.map(c => c.text), labels: [...t.labels], artifacts: [...t.artifacts],
    approval: !!t.approval, approvalMessage: t.approval?.message ?? '', approvers: [...(t.approval?.approvers ?? [])],
    deploy: !!t.deploy, deployModel: { ...(t.deploy ?? { environment: '', compose: '', project: '', manifests: '', namespace: '', url: '' }) },
  })
  nameError.value = null
}
watch(() => props.task.name, () => sync(props.task), { immediate: true })

const otherTasks = computed(() => props.taskNames.filter(n => n !== props.task.name))
const envItems = computed(() => props.environments.map(e => ({ label: `${e.name} · ${e.type === 'SshDocker' ? 'SSH + Docker' : 'Kubernetes'}`, value: e.name })))
const env = computed(() => props.environments.find(e => e.name === s.deployModel.environment) ?? null)

const name = () => props.task.name
const apply = (fn: (doc: Document) => void) => emit('mutate', fn)
const applyDesc = debounce(() => apply(d => tf.setDesc(d, name(), s.desc)), 300)
const applyCmds = debounce(() => apply(d => tf.setCmds(d, name(), s.cmds)), 300)
const applyApproval = debounce(() => apply(d => tf.setApproval(d, name(), s.approval ? { message: s.approvalMessage, approvers: s.approvers } : null)), 300)
const applyDeps = () => apply(d => tf.setDeps(d, name(), s.deps))
const applyLabels = () => apply(d => tf.setLabels(d, name(), s.labels))
const applyArtifacts = () => apply(d => tf.setArtifacts(d, name(), s.artifacts))
const applyDeploy = debounce(() => apply(d => tf.setDeploy(d, name(), s.deploy ? s.deployModel : null)), 300)

function commitName() {
  const to = s.name.trim()
  if (to === props.task.name) { nameError.value = null; return }
  if (!tf.TASK_NAME.test(to)) { nameError.value = 'Letters, digits, _ . : - only'; return }
  if (props.taskNames.includes(to)) { nameError.value = 'A task with this name exists'; return }
  nameError.value = null
  emit('rename', props.task.name, to)
}

function moveCmd(i: number, delta: number) {
  const j = i + delta
  if (j < 0 || j >= s.cmds.length) return
  const next = [...s.cmds];
  [next[i], next[j]] = [next[j], next[i]]
  s.cmds = next
  applyCmds()
}
function removeCmd(i: number) { s.cmds.splice(i, 1); applyCmds() }
function addCmd() { s.cmds.push(''); }

function onDeployToggle(v: boolean) {
  s.deploy = v
  if (v && !s.deployModel.environment && props.environments.length) s.deployModel.environment = props.environments[0].name
  if (v && !s.deployModel.project && !s.deployModel.namespace) s.deployModel.project = props.task.name
  applyDeploy()
}
</script>

<template>
  <div class="space-y-5">
    <div class="flex items-center gap-2">
      <UFormField label="Task name" :error="nameError ?? undefined" class="flex-1">
        <UInput v-model="s.name" class="w-full font-mono" @blur="commitName" @keydown.enter.prevent="commitName" />
      </UFormField>
    </div>
    <div class="flex flex-wrap gap-2">
      <UButton
        v-if="!isEntry" icon="i-lucide-flag" label="Make entry task" size="xs" color="neutral" variant="outline"
        @click="emit('setEntry', task.name)"
      />
      <UBadge v-else icon="i-lucide-flag" label="Entry task" color="primary" variant="subtle" />
      <span class="flex-1" />
      <UButton icon="i-lucide-trash-2" label="Delete task" size="xs" color="error" variant="ghost" @click="emit('remove', task.name)" />
    </div>

    <UFormField label="Description">
      <UInput v-model="s.desc" class="w-full" placeholder="What this task does" @update:model-value="applyDesc" />
    </UFormField>

    <UFormField label="Depends on" help="Dependencies run first, in parallel, possibly on different agents.">
      <USelectMenu
        v-model="s.deps" :items="otherTasks" multiple class="w-full" placeholder="No dependencies"
        :disabled="task.hasComplexDeps" @update:model-value="applyDeps"
      />
      <template v-if="task.hasComplexDeps" #hint><span class="text-warning">Has deps with vars — edit in YAML</span></template>
    </UFormField>

    <UFormField label="Commands" help="Run in order on one agent via go-task.">
      <div v-if="task.simpleCmds" class="space-y-1.5">
        <div v-for="(_c, i) in s.cmds" :key="i" class="flex items-center gap-1">
          <UInput v-model="s.cmds[i]" class="flex-1 font-mono" size="sm" placeholder="shell command" @update:model-value="applyCmds" />
          <UButton icon="i-lucide-chevron-up" size="xs" color="neutral" variant="ghost" :disabled="i === 0" aria-label="Move up" @click="moveCmd(i, -1)" />
          <UButton icon="i-lucide-chevron-down" size="xs" color="neutral" variant="ghost" :disabled="i === s.cmds.length - 1" aria-label="Move down" @click="moveCmd(i, 1)" />
          <UButton icon="i-lucide-x" size="xs" color="neutral" variant="ghost" aria-label="Remove command" @click="removeCmd(i)" />
        </div>
        <UButton icon="i-lucide-plus" label="Add command" size="xs" color="neutral" variant="outline" @click="addCmd" />
      </div>
      <div v-else class="space-y-1">
        <div v-for="(c, i) in task.cmds" :key="i" class="truncate rounded bg-elevated px-2 py-1 font-mono text-xs">
          <span v-if="c.kind === 'task'" class="text-primary">task: </span>{{ c.text }}
        </div>
        <p class="text-xs text-warning">Contains task calls or structured cmds — edit them in the YAML tab.</p>
      </div>
    </UFormField>

    <UFormField label="Agent labels" help="x-agent.labels — the agent must have all of them.">
      <UInputTags v-model="s.labels" class="w-full" placeholder="linux, docker…" @update:model-value="applyLabels" />
    </UFormField>

    <UFormField label="Artifacts" help="x-artifacts — globs uploaded after success and downloaded by dependents.">
      <UInputTags v-model="s.artifacts" class="w-full font-mono" placeholder="out/**" @update:model-value="applyArtifacts" />
    </UFormField>

    <USeparator />

    <div class="space-y-3">
      <USwitch v-model="s.approval" label="Require approval" description="x-approval — the build pauses here until someone approves." @update:model-value="applyApproval" />
      <template v-if="s.approval">
        <UFormField label="Message">
          <UInput v-model="s.approvalMessage" class="w-full" placeholder="Ship to production?" @update:model-value="applyApproval" />
        </UFormField>
        <UFormField label="Approvers" help="User names. Empty = any signed-in user.">
          <UInputTags v-model="s.approvers" class="w-full" @update:model-value="applyApproval" />
        </UFormField>
      </template>
    </div>

    <USeparator />

    <div class="space-y-3">
      <USwitch :model-value="s.deploy" label="Deploy" description="x-deploy — runs Builder's built-in deploy after the commands." @update:model-value="onDeployToggle" />
      <template v-if="s.deploy">
        <UFormField label="Environment">
          <USelect v-if="envItems.length" v-model="s.deployModel.environment" :items="envItems" class="w-full" @update:model-value="applyDeploy" />
          <UInput v-else v-model="s.deployModel.environment" class="w-full" placeholder="environment name" @update:model-value="applyDeploy" />
        </UFormField>
        <div class="grid gap-3 sm:grid-cols-2">
          <template v-if="!env || env.type === 'SshDocker'">
            <UFormField label="Compose file" help="Path in the repo">
              <UInput v-model="s.deployModel.compose" class="w-full font-mono" placeholder="deploy/docker-compose.yml" @update:model-value="applyDeploy" />
            </UFormField>
            <UFormField label="Compose project">
              <UInput v-model="s.deployModel.project" class="w-full font-mono" @update:model-value="applyDeploy" />
            </UFormField>
          </template>
          <template v-if="!env || env.type === 'Kubernetes'">
            <UFormField label="Manifests" help="File or directory in the repo">
              <UInput v-model="s.deployModel.manifests" class="w-full font-mono" placeholder="k8s/" @update:model-value="applyDeploy" />
            </UFormField>
            <UFormField label="Namespace">
              <UInput v-model="s.deployModel.namespace" class="w-full font-mono" @update:model-value="applyDeploy" />
            </UFormField>
          </template>
          <UFormField label="App URL" help="Shown as “Open app”." class="sm:col-span-2">
            <UInput v-model="s.deployModel.url" class="w-full" placeholder="https://app.example.com" @update:model-value="applyDeploy" />
          </UFormField>
        </div>
      </template>
    </div>
  </div>
</template>
