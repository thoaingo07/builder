<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api } from '@/api/client'
import type { AgentDto, CleanupResultDto } from '@/api/types'
import { bytes } from '@/lib/format'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import AgentCleanupModal from '@/components/AgentCleanupModal.vue'
import { useOrgStore } from '@/stores/org'

const live = useLiveStore()
const org = useOrgStore()
const notify = useNotify()
const confirm = useConfirm()

const s = reactive({ olderThanDays: 14, keepLastPerPipeline: 10, removeWorkspaces: true, dockerPrune: false })
const busy = ref(false)
const result = ref<CleanupResultDto | null>(null)
const agentModal = ref(false)
const agent = ref<AgentDto | null>(null)
// shared agents are cleaned up by the operator, not by an organization
const onlineAgents = computed(() => live.agents.filter(a => a.online && !a.shared))

async function run() {
  if (!await confirm({
    title: 'Run cleanup',
    message: `Delete finished builds older than ${s.olderThanDays} days (keeping the newest ${s.keepLastPerPipeline} per runner), with their logs and artifacts.${s.removeWorkspaces || s.dockerPrune ? '\nOnline agents will also clean up.' : ''}`,
    confirmLabel: 'Clean up', danger: true,
  })) return
  busy.value = true
  try {
    result.value = await api.cleanup({ ...s, olderThanDays: Number(s.olderThanDays), keepLastPerPipeline: Number(s.keepLastPerPipeline) })
    notify.success('Cleanup finished')
  } catch (e) { notify.error(e, 'Cleanup failed') } finally { busy.value = false }
}

onMounted(async () => {
  if (!live.agents.length) try { live.setAgents(await api.agents.list()) } catch { /* list below just stays empty */ }
})
</script>

<template>
  <UDashboardPanel id="cleanup">
    <template #header>
      <UDashboardNavbar title="Cleanup" icon="i-lucide-brush-cleaning" />
    </template>

    <template #body>
      <UEmpty
        v-if="!org.isAdmin" icon="i-lucide-shield" title="Admins only"
        description="Cleaning up builds, artifacts and agent workspaces needs the Admin role in this organization."
      />
      <div v-else class="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
        <UCard>
          <template #header>
            <h2 class="font-semibold text-highlighted">Builds, logs & artifacts</h2>
            <p class="text-sm text-muted">Running builds and active deployments are never removed.</p>
          </template>
          <UForm :state="s" class="space-y-5" @submit="run">
            <div class="grid gap-4 sm:grid-cols-2">
              <UFormField label="Older than (days)" name="olderThanDays">
                <UInputNumber v-model="s.olderThanDays" :min="0" class="w-full" />
              </UFormField>
              <UFormField label="Always keep newest per runner" name="keepLastPerPipeline">
                <UInputNumber v-model="s.keepLastPerPipeline" :min="0" class="w-full" />
              </UFormField>
            </div>
            <UCheckbox v-model="s.removeWorkspaces" label="Remove agent workspaces" description="Checked-out sources of finished builds on every online agent." />
            <UCheckbox v-model="s.dockerPrune" label="Docker prune on agents" description="docker system prune -f on every online agent." />
            <UButton type="submit" icon="i-lucide-brush-cleaning" label="Run cleanup" color="error" :loading="busy" />
          </UForm>

          <div v-if="result" class="mt-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
            <div v-for="t in [
              { label: 'Builds deleted', value: result.buildsDeleted },
              { label: 'Artifacts deleted', value: result.artifactsDeleted },
              { label: 'Space freed', value: bytes(result.bytesFreed) },
              { label: 'Agents notified', value: result.agentsNotified },
            ]" :key="t.label" class="rounded-lg border border-default p-3">
              <div class="text-lg font-semibold tabular-nums text-highlighted">{{ t.value }}</div>
              <div class="text-xs text-muted">{{ t.label }}</div>
            </div>
          </div>
        </UCard>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <h2 class="font-semibold text-highlighted">Per agent</h2>
            <p class="text-sm text-muted">Workspaces and Docker on one machine.</p>
          </template>
          <div v-if="!onlineAgents.length" class="p-4 text-sm text-dimmed">No agents online.</div>
          <ul v-else class="divide-y divide-default">
            <li v-for="a in onlineAgents" :key="a.id" class="flex items-center gap-3 px-4 py-3">
              <UChip color="success" standalone inset />
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium">{{ a.name }}</div>
                <div v-if="a.metrics" class="text-xs text-muted tabular-nums">
                  disk {{ bytes(a.metrics.diskUsedBytes) }} / {{ bytes(a.metrics.diskTotalBytes) }}
                </div>
              </div>
              <UButton size="xs" color="neutral" variant="outline" icon="i-lucide-brush-cleaning" label="Clean" @click="agent = a; agentModal = true" />
            </li>
          </ul>
        </UCard>
      </div>
      <AgentCleanupModal v-model:open="agentModal" :agent="agent" />
    </template>
  </UDashboardPanel>
</template>
