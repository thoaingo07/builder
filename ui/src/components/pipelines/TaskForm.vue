<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { Document } from 'yaml'
import type { ConnectionDto, EnvironmentDto } from '@/api/types'
import * as tf from '@/lib/taskfile'
import { debounce } from '@/lib/collections'
import StepsEditor from './StepsEditor.vue'
import KeyValueEditor from './KeyValueEditor.vue'
import RequiresEditor from './RequiresEditor.vue'

const props = defineProps<{
  task: tf.TaskModel; taskNames: string[]; environments: EnvironmentDto[]; secretNames: string[]
  connections: ConnectionDto[]; isEntry: boolean
}>()
const emit = defineEmits<{
  mutate: [fn: (doc: Document) => void]
  rename: [from: string, to: string]
  remove: [name: string]
  setEntry: [name: string]
}>()

// Local copy so typing isn't interrupted by the YAML round-trip; re-synced when another task is selected.
const s = reactive({
  name: '', desc: '', deps: [] as string[], labels: [] as string[], artifacts: [] as string[], secrets: [] as string[],
  vars: [] as tf.KeyValue[], env: [] as tf.KeyValue[], requires: [] as tf.RequiredVar[],
  registries: [] as tf.RegistryModel[], azureArtifacts: false,
  approval: false, approvalMessage: '', approvers: [] as string[],
  deploy: false, deployModel: { environment: '', compose: '', project: '', manifests: '', namespace: '', url: '' } as tf.DeployModel,
})
const nameError = ref<string | null>(null)

function sync(t: tf.TaskModel) {
  Object.assign(s, {
    name: t.name, desc: t.desc, deps: [...t.deps], labels: [...t.labels], artifacts: [...t.artifacts], secrets: [...t.secrets],
    vars: t.vars.map(v => ({ ...v })), env: t.env.map(v => ({ ...v })), requires: t.requires.map(r => ({ name: r.name, enum: [...r.enum] })),
    registries: t.registries.map(r => ({ ...r })), azureArtifacts: t.azureArtifacts,
    approval: !!t.approval, approvalMessage: t.approval?.message ?? '', approvers: [...(t.approval?.approvers ?? [])],
    deploy: !!t.deploy, deployModel: { ...(t.deploy ?? { environment: '', compose: '', project: '', manifests: '', namespace: '', url: '' }) },
  })
  nameError.value = null
}
watch(() => props.task.name, () => sync(props.task), { immediate: true })

const otherTasks = computed(() => props.taskNames.filter(n => n !== props.task.name))
const envItems = computed(() => props.environments.map(e => ({ label: `${e.name} · ${e.type === 'SshDocker' ? 'SSH + Compose' : 'Kubernetes'}`, value: e.name })))
const env = computed(() => props.environments.find(e => e.name === s.deployModel.environment) ?? null)

const name = () => props.task.name
const apply = (fn: (doc: Document) => void) => emit('mutate', fn)
const applyDesc = debounce(() => apply(d => tf.setDesc(d, name(), s.desc)), 300)
const applyVars = debounce(() => apply(d => tf.setKeyValues(d, name(), 'vars', s.vars)), 350)
const applyEnv = debounce(() => apply(d => tf.setKeyValues(d, name(), 'env', s.env)), 350)
const applyRequires = debounce(() => apply(d => tf.setRequires(d, name(), s.requires)), 350)
const applyApproval = debounce(() => apply(d => tf.setApproval(d, name(), s.approval ? { message: s.approvalMessage, approvers: s.approvers } : null)), 300)
const applyDeps = () => apply(d => tf.setDeps(d, name(), s.deps))
const applyLabels = () => apply(d => tf.setLabels(d, name(), s.labels))
const applyArtifacts = () => apply(d => tf.setArtifacts(d, name(), s.artifacts))
const applySecrets = () => apply(d => tf.setSecrets(d, name(), s.secrets))
// names referenced in the file but not defined in the organization still show (and get flagged)
const secretItems = computed(() => [...new Set([...props.secretNames, ...s.secrets])].sort())
const applyRegistries = debounce(() => apply(d => tf.setRegistries(d, name(), s.registries)), 300)
const applyAzureArtifacts = () => apply(d => tf.setAzureArtifacts(d, name(), s.azureArtifacts))
const NO_CONNECTION = '__none__'
// registry logins go through Azure (ACR) or Azure DevOps connections
const registryConnectionItems = computed(() => [
  { label: 'No connection', value: NO_CONNECTION },
  ...props.connections.filter(c => c.type === 'Azure').map(c => ({ label: `${c.name} · Azure`, value: c.name })),
])
function setRegistryConnection(i: number, v: string) {
  s.registries[i].connection = v === NO_CONNECTION ? '' : v
  applyRegistries()
}
function addRegistry() { s.registries.push({ registry: '', connection: '' }) }
function removeRegistry(i: number) { s.registries.splice(i, 1); applyRegistries() }
const unknownSecrets = computed(() => s.secrets.filter(x => !props.secretNames.includes(x)))
const applyDeploy = debounce(() => apply(d => tf.setDeploy(d, name(), s.deploy ? s.deployModel : null)), 300)

function commitName() {
  const to = s.name.trim()
  if (to === props.task.name) { nameError.value = null; return }
  if (!tf.TASK_NAME.test(to)) { nameError.value = 'Letters, digits, _ . : - only'; return }
  if (props.taskNames.includes(to)) { nameError.value = 'A task with this name exists'; return }
  nameError.value = null
  emit('rename', props.task.name, to)
}



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

    <UFormField label="Steps" help="cmds — run in order on one agent; each one is a step in the build log.">
      <StepsEditor :task-name="task.name" :steps="task.steps" :other-tasks="otherTasks" @mutate="fn => emit('mutate', fn)" />
    </UFormField>

    <UFormField label="Variables" help="vars — available to every step as {{.NAME}}.">
      <KeyValueEditor v-model="s.vars" add-label="Add variable" @update:model-value="applyVars" />
    </UFormField>

    <UFormField label="Environment" help="env — exported to every step's shell.">
      <KeyValueEditor v-model="s.env" add-label="Add env var" @update:model-value="applyEnv" />
    </UFormField>

    <UFormField label="Run inputs" help="requires.vars — the Run dialog asks for these; leave the value list empty to accept anything.">
      <RequiresEditor v-model="s.requires" @update:model-value="applyRequires" />
    </UFormField>

    <UFormField label="Agent labels" help="x-agent.labels — the agent must have all of them.">
      <UInputTags v-model="s.labels" class="w-full" placeholder="linux, docker…" @update:model-value="applyLabels" />
    </UFormField>

    <UFormField label="Secrets" help="x-secrets — passed to the task as {{.NAME}} vars and $NAME env, masked in logs.">
      <USelectMenu
        v-model="s.secrets" :items="secretItems" multiple class="w-full font-mono" placeholder="No secrets"
        icon="i-lucide-key-round" @update:model-value="applySecrets"
      />
      <template v-if="unknownSecrets.length" #hint>
        <span class="text-warning">Not defined in this organization: {{ unknownSecrets.join(', ') }}</span>
      </template>
    </UFormField>

    <UFormField label="Container registries" help="x-registries — the agent logs in before the commands run; tokens are minted per job through the connection.">
      <div class="space-y-1.5">
        <div v-for="(r, i) in s.registries" :key="i" class="flex items-center gap-1">
          <UInput v-model="r.registry" size="sm" class="min-w-0 flex-1 font-mono" placeholder="contoso.azurecr.io" @update:model-value="applyRegistries" />
          <USelect
            :model-value="r.connection || NO_CONNECTION" :items="registryConnectionItems" size="sm" class="w-40"
            @update:model-value="(v: string) => setRegistryConnection(i, v)"
          />
          <UButton icon="i-lucide-x" size="xs" color="neutral" variant="ghost" aria-label="Remove registry" @click="removeRegistry(i)" />
        </div>
        <UButton icon="i-lucide-plus" label="Add registry" size="xs" color="neutral" variant="outline" @click="addRegistry" />
      </div>
    </UFormField>

    <USwitch
      v-model="s.azureArtifacts" label="Azure Artifacts token" description="x-azure-artifacts — token in $VSS_NUGET_ACCESSTOKEN / $AZURE_DEVOPS_TOKEN."
      @update:model-value="applyAzureArtifacts"
    />

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
