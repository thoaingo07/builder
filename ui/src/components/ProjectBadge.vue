<script setup lang="ts">
import { useProjectStore } from '@/stores/project'

/** Project name for rows of a mixed list; hidden when a single project is selected. */
defineProps<{ projectId: string | null | undefined }>()
const project = useProjectStore()
</script>

<template>
  <UBadge
    v-if="!project.currentId && projectId && project.projects.length > 1" :label="project.nameOf(projectId)"
    icon="i-lucide-folder-kanban" size="sm" color="neutral" variant="soft" class="max-w-36"
  />
</template>
