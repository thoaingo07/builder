<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { api } from '@/api/client'
import type { PipelineDto } from '@/api/types'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ runner: PipelineDto | null }>()
const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ saved: [PipelineDto] }>()
const notify = useNotify()
const s = reactive({ name: '', entryTask: '' })
const saving = ref(false)

watch(open, o => {
  if (o && props.runner) Object.assign(s, { name: props.runner.name, entryTask: props.runner.entryTask ?? '' })
})

async function submit() {
  if (!props.runner || !s.name.trim()) return
  saving.value = true
  try {
    const saved = await api.pipelines.update(props.runner.id, { name: s.name.trim(), entryTask: s.entryTask.trim() || null })
    notify.success('Runner updated')
    emit('saved', saved)
    open.value = false
  } catch (e) {
    notify.error(e, 'Could not save runner')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" :title="`Edit ${runner?.name ?? 'runner'}`" :description="runner ? `${runner.repositoryName} · ${runner.taskfilePath}` : undefined">
    <template #body>
      <form id="runner-form" class="space-y-4" @submit.prevent="submit">
        <UFormField label="Name" required>
          <UInput v-model="s.name" class="w-full" />
        </UFormField>
        <UFormField label="Entry task" help="Empty = the file's x-builder.entry, then 'default'.">
          <UInput v-model="s.entryTask" class="w-full font-mono" placeholder="ci" />
        </UFormField>
      </form>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="runner-form" label="Save" :loading="saving" :disabled="!s.name.trim()" />
      </div>
    </template>
  </UModal>
</template>
