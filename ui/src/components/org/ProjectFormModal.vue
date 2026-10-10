<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { api } from '@/api/client'
import type { ProjectDto } from '@/api/types'
import { useProjectStore } from '@/stores/project'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ project: ProjectDto | null }>()
const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ saved: [ProjectDto] }>()
const store = useProjectStore()
const notify = useNotify()
const s = reactive({ name: '', description: '' })
const saving = ref(false)

watch(open, o => { if (o) Object.assign(s, { name: props.project?.name ?? '', description: props.project?.description ?? '' }) })

async function submit() {
  if (!s.name.trim()) return
  saving.value = true
  try {
    const input = { name: s.name.trim(), description: s.description.trim() || null }
    const saved = props.project ? await api.projects.update(props.project.id, input) : await api.projects.create(input)
    store.upsert(saved)
    notify.success(props.project ? 'Project updated' : `Created ${saved.name}`)
    emit('saved', saved)
    open.value = false
  } catch (e) {
    notify.error(e, 'Could not save project')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal
    v-model:open="open" :title="project ? `Edit ${project.name}` : 'New project'"
    description="A project groups repositories and runners, with its own connections, environments and secrets."
  >
    <template #body>
      <form id="project-form" class="space-y-4" @submit.prevent="submit">
        <UFormField label="Name" required>
          <UInput v-model="s.name" class="w-full" placeholder="Shop" autofocus />
        </UFormField>
        <UFormField label="Description">
          <UTextarea v-model="s.description" :rows="2" autoresize class="w-full" placeholder="What lives here" />
        </UFormField>
      </form>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="project-form" :label="project ? 'Save' : 'Create'" :loading="saving" :disabled="!s.name.trim()" />
      </div>
    </template>
  </UModal>
</template>
