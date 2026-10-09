<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api/client'
import type { PipelineDto } from '@/api/types'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ pipeline: PipelineDto | null }>()
const open = defineModel<boolean>('open', { required: true })

const router = useRouter()
const notify = useNotify()
const state = reactive({ branch: '', entryTask: '' })
const vars = ref<{ key: string; value: string }[]>([])
const busy = ref(false)

watch(open, o => {
  if (!o || !props.pipeline) return
  state.branch = props.pipeline.defaultBranch
  state.entryTask = props.pipeline.entryTask ?? ''
  vars.value = []
})

async function run() {
  if (!props.pipeline) return
  busy.value = true
  try {
    const variables = Object.fromEntries(vars.value.filter(v => v.key.trim()).map(v => [v.key.trim(), v.value]))
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
  <UModal v-model:open="open" :title="`Run ${pipeline?.name ?? ''}`" description="Queue a new build.">
    <template #body>
      <UForm id="run-form" :state="state" class="space-y-4" @submit="run">
        <div class="grid gap-4 sm:grid-cols-2">
          <UFormField label="Branch" name="branch">
            <UInput v-model="state.branch" icon="i-lucide-git-branch" class="w-full" />
          </UFormField>
          <UFormField label="Entry task" name="entryTask" :help="pipeline?.entryTask ? undefined : 'Empty = Taskfile default'">
            <UInput v-model="state.entryTask" class="w-full font-mono" placeholder="ci" />
          </UFormField>
        </div>
        <UFormField label="Variables" help="Passed to go-task as KEY=value.">
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
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="run-form" icon="i-lucide-play" label="Run" :loading="busy" />
      </div>
    </template>
  </UModal>
</template>
