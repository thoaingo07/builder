<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { api } from '@/api/client'
import type { AgentDto } from '@/api/types'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ agent: AgentDto | null }>()
const open = defineModel<boolean>('open', { required: true })
const notify = useNotify()
const state = reactive({ removeWorkspaces: true, dockerPrune: false })
const busy = ref(false)

watch(open, o => { if (o) Object.assign(state, { removeWorkspaces: true, dockerPrune: false }) })

async function run() {
  if (!props.agent) return
  busy.value = true
  try {
    await api.agents.cleanup(props.agent.id, state.removeWorkspaces, state.dockerPrune)
    notify.info(`Cleanup sent to ${props.agent.name}`, 'It runs in the background on the agent.')
    open.value = false
  } catch (e) {
    notify.error(e, 'Cleanup failed')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" :title="`Clean up ${agent?.name ?? ''}`" description="Frees disk space on the agent. Running builds are never touched.">
    <template #body>
      <div class="space-y-4">
        <UCheckbox v-model="state.removeWorkspaces" label="Remove build workspaces" description="Deletes checked-out sources of finished builds." />
        <UCheckbox v-model="state.dockerPrune" label="Docker prune" description="docker system prune -f: stopped containers, dangling images, unused networks and build cache." />
      </div>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton icon="i-lucide-brush-cleaning" label="Clean up" :loading="busy" :disabled="!state.removeWorkspaces && !state.dockerPrune" @click="run" />
      </div>
    </template>
  </UModal>
</template>
