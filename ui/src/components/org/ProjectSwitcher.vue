<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import type { DropdownMenuItem } from '@nuxt/ui'
import type { ProjectDto } from '@/api/types'
import { useProjectStore } from '@/stores/project'
import { useOrgStore } from '@/stores/org'
import ProjectFormModal from './ProjectFormModal.vue'

defineProps<{ collapsed?: boolean }>()

const store = useProjectStore()
const org = useOrgStore()
const router = useRouter()
const createOpen = ref(false)

async function choose(id: string | null) {
  if (id === store.currentId) return
  store.select(id)
  // detail pages may belong to another project
  if (Object.keys(router.currentRoute.value.params).length) await router.push('/')
}

const items = computed<DropdownMenuItem[][]>(() => [
  [{ label: 'All projects', icon: store.currentId ? 'i-lucide-layers' : 'i-lucide-check', onSelect: () => void choose(null) }],
  store.projects.map(p => ({
    label: p.name,
    description: `${p.repositoryCount} repo${p.repositoryCount === 1 ? '' : 's'} · ${p.runnerCount} runner${p.runnerCount === 1 ? '' : 's'}`,
    icon: p.id === store.currentId ? 'i-lucide-check' : 'i-lucide-folder-kanban',
    onSelect: () => void choose(p.id),
  })),
  [
    ...(org.isAdmin ? [{ label: 'New project…', icon: 'i-lucide-plus', onSelect: () => { createOpen.value = true } }] : []),
    { label: 'Manage projects', icon: 'i-lucide-settings-2', to: '/projects' },
  ],
].filter(g => g.length))

function created(p: ProjectDto) { void choose(p.id) }
</script>

<template>
  <UDropdownMenu :items="items" :content="{ align: 'start', collisionPadding: 12 }" :ui="{ content: 'min-w-60 max-h-[70vh]' }">
    <UButton
      color="neutral" variant="outline" block :square="collapsed" class="data-[state=open]:bg-elevated"
      :icon="store.current ? 'i-lucide-folder-kanban' : 'i-lucide-layers'"
      :trailing-icon="collapsed ? undefined : 'i-lucide-chevrons-up-down'" :ui="{ trailingIcon: 'text-dimmed ms-auto' }"
      :aria-label="`Project: ${store.current?.name ?? 'All projects'}`"
    >
      <span v-if="!collapsed" class="truncate">{{ store.current?.name ?? 'All projects' }}</span>
    </UButton>
  </UDropdownMenu>
  <ProjectFormModal v-model:open="createOpen" :project="null" @saved="created" />
</template>
