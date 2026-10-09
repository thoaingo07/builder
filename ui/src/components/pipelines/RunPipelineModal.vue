<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError } from '@/api/client'
import type { PipelineDto, RunInputDto } from '@/api/types'
import { debounce } from '@/lib/collections'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ pipeline: PipelineDto | null }>()
const open = defineModel<boolean>('open', { required: true })

const router = useRouter()
const notify = useNotify()
const state = reactive({ branch: '', entryTask: '' })
const vars = ref<{ key: string; value: string }[]>([])
const busy = ref(false)
const branches = ref<string[]>([])
const loadingBranches = ref(false)

// ---------- run inputs (go-task requires.vars) ----------
const inputs = ref<RunInputDto[]>([])
const fileEntryTask = ref<string | null>(null)
const inputValues = reactive<Record<string, string>>({})
const inputsError = ref<string | null>(null)
const loadingInputs = ref(false)
const submitted = ref(false)
let inputsSeq = 0

async function loadInputs() {
  const p = props.pipeline
  if (!p || !open.value) return
  const seq = ++inputsSeq
  loadingInputs.value = true
  inputsError.value = null
  try {
    const r = await api.pipelines.inputs(p.id, state.branch.trim() || null, state.entryTask.trim() || null)
    if (seq !== inputsSeq) return
    inputs.value = r.inputs
    fileEntryTask.value = r.entryTask
    for (const i of r.inputs) {
      // keep what the user already typed; preselect a lone allowed value
      if (inputValues[i.name] === undefined) inputValues[i.name] = i.enum?.length === 1 ? i.enum[0] : ''
      else if (i.enum?.length && !i.enum.includes(inputValues[i.name])) inputValues[i.name] = ''
    }
  } catch (e) {
    if (seq !== inputsSeq) return
    inputs.value = []
    inputsError.value = e instanceof ApiError ? (e.problem.detail ?? e.problem.title ?? e.message) : (e as Error).message
  } finally {
    if (seq === inputsSeq) loadingInputs.value = false
  }
}
const loadInputsSoon = debounce(() => void loadInputs(), 400)
watch(() => [state.branch, state.entryTask], () => { if (open.value) loadInputsSoon() })

const missing = computed(() => inputs.value.filter(i => !(inputValues[i.name] ?? '').trim()).map(i => i.name))
const enumItems = (i: RunInputDto) => (i.enum ?? []).map(v => ({ label: v, value: v }))

watch(open, async o => {
  if (!o || !props.pipeline) return
  state.branch = props.pipeline.defaultBranch
  state.entryTask = props.pipeline.entryTask ?? ''
  vars.value = []
  inputs.value = []
  inputsError.value = null
  submitted.value = false
  for (const k of Object.keys(inputValues)) delete inputValues[k]
  branches.value = []
  loadingBranches.value = true
  try {
    branches.value = await api.repositories.branches(props.pipeline.repositoryId)
    if (!branches.value.includes(state.branch) && branches.value.length) state.branch = branches.value[0]
  } catch { /* free-text branch still works */ } finally { loadingBranches.value = false }
  void loadInputs()
})

async function run() {
  if (!props.pipeline) return
  submitted.value = true
  if (missing.value.length) return
  busy.value = true
  try {
    const variables: Record<string, string> = Object.fromEntries(vars.value.filter(v => v.key.trim()).map(v => [v.key.trim(), v.value]))
    // declared inputs win over free-form variables with the same name
    for (const i of inputs.value) variables[i.name] = inputValues[i.name].trim()
    const b = await api.pipelines.run(props.pipeline.id, {
      branch: state.branch.trim() || null, entryTask: state.entryTask.trim() || null, variables,
    })
    notify.success(`Queued ${b.pipelineName} #${b.number}`)
    open.value = false
    await router.push(`/builds/${b.id}`)
  } catch (e) {
    notify.error(e, 'Could not start build')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" :title="`Run ${pipeline?.name ?? ''}`" :description="pipeline ? `${pipeline.repositoryName} · ${pipeline.taskfilePath}` : undefined">
    <template #body>
      <UForm id="run-form" :state="state" class="space-y-4" @submit="run">
        <div class="grid gap-4 sm:grid-cols-2">
          <UFormField label="Branch" name="branch">
            <USelectMenu
              v-if="branches.length" v-model="state.branch" :items="branches" icon="i-lucide-git-branch" class="w-full"
              :create-item="{ position: 'bottom' }" @create="(b: string) => { branches.push(b); state.branch = b }"
            />
            <UInput v-else v-model="state.branch" icon="i-lucide-git-branch" class="w-full" :loading="loadingBranches" />
          </UFormField>
          <UFormField label="Entry task" name="entryTask" :help="pipeline?.entryTask ? undefined : 'Empty = the file\'s entry task'">
            <UInput v-model.lazy="state.entryTask" class="w-full font-mono" :placeholder="fileEntryTask ?? 'the file\'s entry task'" />
          </UFormField>
        </div>

        <div class="space-y-3">
          <div class="flex items-center gap-2 text-sm font-medium">
            Inputs
            <UIcon v-if="loadingInputs" name="i-lucide-loader-circle" class="size-4 animate-spin text-muted" />
          </div>
          <UAlert
            v-if="inputsError" color="warning" variant="subtle" icon="i-lucide-triangle-alert"
            title="Could not read the runner's inputs" :description="`${inputsError} You can still pass variables below.`"
          />
          <p v-else-if="!loadingInputs && !inputs.length" class="text-xs text-muted">This runner declares no required inputs (requires.vars).</p>
          <UFormField
            v-for="i in inputs" :key="i.name" :label="i.name" required
            :help="`Required by: ${i.requiredBy.join(', ')}`"
            :error="submitted && !(inputValues[i.name] ?? '').trim() ? 'Required' : undefined"
          >
            <USelect v-if="i.enum?.length" v-model="inputValues[i.name]" :items="enumItems(i)" class="w-full font-mono" placeholder="Choose a value" />
            <UInput v-else v-model="inputValues[i.name]" class="w-full font-mono" />
          </UFormField>
        </div>

        <UFormField label="More variables" help="Passed to go-task as KEY=value.">
          <div class="space-y-2">
            <div v-for="(v, i) in vars" :key="i" class="flex gap-2">
              <UInput v-model="v.key" placeholder="KEY" class="flex-1 font-mono" />
              <UInput v-model="v.value" placeholder="value" class="flex-1 font-mono" />
              <UButton icon="i-lucide-x" color="neutral" variant="ghost" aria-label="Remove variable" @click="vars.splice(i, 1)" />
            </div>
            <UButton icon="i-lucide-plus" label="Add variable" size="xs" color="neutral" variant="outline" @click="vars.push({ key: '', value: '' })" />
          </div>
        </UFormField>
      </UForm>
    </template>
    <template #footer>
      <div class="flex w-full items-center justify-end gap-2">
        <span v-if="submitted && missing.length" class="mr-auto text-xs text-error">Missing: {{ missing.join(', ') }}</span>
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="run-form" icon="i-lucide-play" label="Run" :loading="busy" />
      </div>
    </template>
  </UModal>
</template>
