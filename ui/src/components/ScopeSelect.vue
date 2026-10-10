<script setup lang="ts">
import { computed } from 'vue'
import { useProjectStore } from '@/stores/project'

/** Scope of a connection/environment/secret: one project, or shared by all (null). */
const model = defineModel<string | null>({ required: true })
const project = useProjectStore()
const SHARED = '__shared__'

const items = computed(() => [
  ...project.projects.map(p => ({ label: p.id === project.currentId ? `${p.name} (this project)` : p.name, value: p.id, icon: 'i-lucide-folder-kanban' })),
  { label: 'Shared with all projects', value: SHARED, icon: 'i-lucide-globe' },
])
const value = computed({
  get: () => model.value ?? SHARED,
  set: (v: string) => { model.value = v === SHARED ? null : v },
})
</script>

<template>
  <UFormField label="Scope" help="A project's runners use its own item first, then a shared one with the same name.">
    <USelect v-model="value" :items="items" class="w-full" />
  </UFormField>
</template>
