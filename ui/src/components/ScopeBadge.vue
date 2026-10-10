<script setup lang="ts">
import { useProjectStore } from '@/stores/project'

/** Where a connection/environment/secret lives: a project, or shared by all projects. */
withDefaults(defineProps<{ projectId: string | null; overrides?: boolean; scope?: boolean }>(), { overrides: false, scope: true })
const project = useProjectStore()
</script>

<template>
  <span class="inline-flex flex-wrap items-center gap-1">
    <UBadge
      v-if="scope && projectId" :label="project.nameOf(projectId)" icon="i-lucide-folder-kanban" size="sm" color="primary" variant="subtle"
      class="max-w-40"
    />
    <UBadge v-else-if="scope" label="Shared" icon="i-lucide-globe" size="sm" color="neutral" variant="outline" />
    <UTooltip v-if="overrides" text="A shared item has the same name; this project's runners use this one instead.">
      <UBadge label="overrides shared" icon="i-lucide-layers" size="sm" color="warning" variant="subtle" />
    </UTooltip>
  </span>
</template>
