<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '@/api/client'
import type { AgentDto } from '@/api/types'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ agent: AgentDto | null }>()
const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ saved: [AgentDto] }>()
const notify = useNotify()
const folder = ref('')
const busy = ref(false)

watch(open, o => { if (o) folder.value = props.agent?.workDirectory ?? '' })

async function save(value: string) {
  if (!props.agent) return
  busy.value = true
  try {
    const updated = await api.agents.setWorkDirectory(props.agent, value.trim())
    emit('saved', updated)
    if (updated.workDirectoryError) notify.error(updated.workDirectoryError, 'The agent kept its folder')
    else if (!updated.online) notify.info('Saved', `${updated.name} is offline; it switches when it connects.`)
    else notify.success('Work folder updated', `${updated.name} uses ${updated.effectiveWorkDirectory} for new jobs.`)
    if (!updated.workDirectoryError) open.value = false
  } catch (e) {
    notify.error(e, 'Could not change the work folder')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" :title="`Work folder · ${agent?.name ?? ''}`" description="Where this agent keeps checkouts, job sandboxes and temporary files.">
    <template #body>
      <div class="space-y-4">
        <UFormField label="Folder on the agent's machine" help="Absolute path. Leave empty to use the folder from the agent's own configuration.">
          <UInput v-model="folder" class="w-full font-mono" :placeholder="agent?.defaultWorkDirectory ?? '/srv/builder'" autocomplete="off" spellcheck="false" @keydown.enter="save(folder)" />
        </UFormField>
        <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
          <dt class="text-muted">In use now</dt>
          <dd class="break-all font-mono">{{ agent?.effectiveWorkDirectory ?? '—' }}</dd>
          <dt class="text-muted">Agent's default</dt>
          <dd class="break-all font-mono">{{ agent?.defaultWorkDirectory ?? '—' }}</dd>
        </dl>
        <UAlert
          color="neutral" variant="subtle" icon="i-lucide-info"
          description="The agent creates the folder and checks it can write there before switching. Running jobs finish where they are; nothing is moved or deleted from the old folder."
          :ui="{ description: 'text-xs' }"
        />
      </div>
    </template>
    <template #footer>
      <div class="flex w-full flex-wrap justify-end gap-2">
        <UButton v-if="agent?.workDirectory" label="Use agent's default" color="neutral" variant="ghost" :disabled="busy" @click="save('')" />
        <UButton label="Cancel" color="neutral" variant="outline" :disabled="busy" @click="open = false" />
        <UButton label="Save" :loading="busy" @click="save(folder)" />
      </div>
    </template>
  </UModal>
</template>
