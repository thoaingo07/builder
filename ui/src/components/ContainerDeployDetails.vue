<script setup lang="ts">
import { computed } from 'vue'
import type { ContainerDeployDto } from '@/api/types'

/** Single-container deploy (x-deploy.strategy): how the new container is started and checked. */
const props = defineProps<{ c: ContainerDeployDto }>()

const health = computed(() => {
  const c = props.c
  if (c.healthPath) return `${c.healthScheme}://${c.service}:${c.healthPort}${c.healthPath.startsWith('/') ? '' : '/'}${c.healthPath}`
  if (c.healthPort) return `port ${c.healthPort} accepts connections`
  return 'image HEALTHCHECK'
})
</script>

<template>
  <div class="space-y-2 text-xs">
    <div class="flex flex-wrap items-center gap-1.5">
      <UBadge
        :label="c.strategy === 'BlueGreen' ? 'Blue-green' : 'Recreate'" size="sm" variant="subtle"
        :color="c.strategy === 'BlueGreen' ? 'primary' : 'neutral'" :icon="c.strategy === 'BlueGreen' ? 'i-lucide-arrow-left-right' : 'i-lucide-refresh-cw'"
      />
      <span class="text-muted">service alias</span><code class="font-semibold">{{ c.service }}</code>
    </div>
    <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
      <dt class="text-muted">Image</dt><dd class="font-mono break-all">{{ c.image }}</dd>
      <dt class="text-muted">Network</dt><dd class="font-mono break-all">{{ c.network }}</dd>
      <template v-if="c.envFile"><dt class="text-muted">Env file</dt><dd class="font-mono break-all">{{ c.envFile }}</dd></template>
      <template v-if="c.args.length">
        <dt class="pt-0.5 text-muted">Args</dt>
        <dd class="flex flex-wrap gap-1"><UBadge v-for="(a, i) in c.args" :key="i" :label="a" size="sm" color="neutral" variant="soft" class="max-w-full font-mono" /></dd>
      </template>
      <template v-if="c.command?.length">
        <dt class="pt-0.5 text-muted">Command</dt>
        <dd class="flex flex-wrap gap-1"><UBadge v-for="(a, i) in c.command" :key="i" :label="a" size="sm" color="neutral" variant="outline" class="max-w-full font-mono" /></dd>
      </template>
      <dt class="text-muted">Health</dt><dd><span class="font-mono break-all">{{ health }}</span> <span class="whitespace-nowrap text-muted">· timeout {{ c.timeoutSeconds }}s</span></dd>
      <dt class="text-muted">Keep</dt><dd>{{ c.keep }} previous container{{ c.keep === 1 ? '' : 's' }} for rollback</dd>
    </dl>
  </div>
</template>
