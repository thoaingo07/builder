<script setup lang="ts">
import NavAction from '@/components/NavAction.vue'
import { computed, onMounted, ref } from 'vue'
import type { DropdownMenuItem } from '@nuxt/ui'
import { api } from '@/api/client'
import type { AgentDto } from '@/api/types'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import AgentCard from '@/components/AgentCard.vue'
import AgentCleanupModal from '@/components/AgentCleanupModal.vue'
import AgentWorkFolderModal from '@/components/AgentWorkFolderModal.vue'
import { useAuthStore } from '@/stores/auth'
import AgentTokenModal from '@/components/org/AgentTokenModal.vue'
import { useOrgStore } from '@/stores/org'

const live = useLiveStore()
const notify = useNotify()
const confirm = useConfirm()
const loading = ref(true)
const cleanupOpen = ref(false)
const cleanupAgent = ref<AgentDto | null>(null)
const org = useOrgStore()
const tokenOpen = ref(false)
const token = ref<string | null>(null)
const regenerating = ref(false)
const folderOpen = ref(false)
const folderAgent = ref<AgentDto | null>(null)
const auth = useAuthStore()

/** Org admins manage their organization's agents; Builder admins also enable/disable and set the folder of shared ones. */
const manageable = (a: AgentDto) => org.isAdmin && (!a.shared || !!auth.user?.isAdmin)

function addAgent() {
  token.value = null
  tokenOpen.value = true
}

async function regenerate() {
  if (!await confirm({
    title: 'Regenerate agent token',
    message: 'A new token is created and shown once. Agents using the old token stop connecting until they are reconfigured.',
    confirmLabel: 'Regenerate', danger: true,
  })) return
  regenerating.value = true
  try {
    token.value = (await api.org.regenerateAgentToken()).agentToken
    tokenOpen.value = true
  } catch (e) { notify.error(e, 'Could not regenerate the token') } finally { regenerating.value = false }
}

const sorted = computed(() => [...live.agents].sort((a, b) =>
  Number(a.shared) - Number(b.shared) || Number(b.online) - Number(a.online) || a.name.localeCompare(b.name)))
const online = computed(() => live.agents.filter(a => a.online).length)

async function load() {
  try {
    const list = await api.agents.list()
    live.setAgents(list)
    for (const a of list) void live.loadHistory(a.id)
  } catch (e) { notify.error(e, 'Could not load agents') } finally { loading.value = false }
}

async function toggle(a: AgentDto) {
  try {
    const updated = await api.agents.setEnabled(a.id, !a.enabled)
    live.setAgents(live.agents.map(x => (x.id === a.id ? updated : x)))
    notify.success(`${a.name} ${updated.enabled ? 'enabled' : 'disabled'}`, updated.enabled ? undefined : 'Running jobs finish; no new jobs are assigned.')
  } catch (e) { notify.error(e, 'Update failed') }
}

async function remove(a: AgentDto) {
  if (!await confirm({ title: 'Remove agent', message: `Remove "${a.name}"? If it reconnects it registers again.`, confirmLabel: 'Remove', danger: true })) return
  try {
    await api.agents.remove(a.id)
    live.setAgents(live.agents.filter(x => x.id !== a.id))
    notify.success(`Removed ${a.name}`)
  } catch (e) { notify.error(e, 'Remove failed') }
}

function menu(a: AgentDto): DropdownMenuItem[][] {
  return [
    [
      { label: a.enabled ? 'Disable' : 'Enable', icon: a.enabled ? 'i-lucide-pause' : 'i-lucide-play', onSelect: () => void toggle(a) },
      { label: 'Work folder…', icon: 'i-lucide-folder-cog', onSelect: () => { folderAgent.value = a; folderOpen.value = true } },
      ...(a.shared ? [] : [{ label: 'Clean up…', icon: 'i-lucide-brush-cleaning', disabled: !a.online, onSelect: () => { cleanupAgent.value = a; cleanupOpen.value = true } }]),
    ],
    ...(a.shared ? [] : [[{ label: 'Remove', icon: 'i-lucide-trash-2', color: 'error' as const, disabled: a.online, onSelect: () => void remove(a) }]]),
  ]
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="agents">
    <template #header>
      <UDashboardNavbar title="Agents" icon="i-lucide-server">
        <template #trailing>
          <UBadge :label="`${online}/${live.agents.length} online`" color="neutral" variant="subtle" />
        </template>
        <template #right>
          <UButton icon="i-lucide-refresh-cw" color="neutral" variant="ghost" aria-label="Refresh" @click="load" />
          <NavAction v-if="org.isAdmin" icon="i-lucide-key-round" label="New token" color="neutral" variant="outline" :loading="regenerating" @click="regenerate" />
          <NavAction icon="i-lucide-plus" label="Add agent" @click="addAgent" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div v-if="loading" class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        <USkeleton v-for="i in 3" :key="i" class="h-60" />
      </div>
      <UEmpty
        v-else-if="!live.agents.length" icon="i-lucide-server-off" title="No agents registered"
        description="Start a Builder agent with this organization's token; it registers itself on first connect."
        :actions="[{ label: 'Add agent', icon: 'i-lucide-plus', onClick: addAgent }]"
      />
      <div v-else class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        <AgentCard v-for="a in sorted" :key="a.id" :agent="a">
          <template v-if="manageable(a)" #actions>
            <UDropdownMenu :items="menu(a)" :content="{ align: 'end' }">
              <UButton icon="i-lucide-ellipsis-vertical" color="neutral" variant="ghost" size="xs" aria-label="Agent actions" />
            </UDropdownMenu>
          </template>
        </AgentCard>
      </div>
      <AgentCleanupModal v-model:open="cleanupOpen" :agent="cleanupAgent" />
      <AgentWorkFolderModal v-model:open="folderOpen" :agent="folderAgent" @saved="u => live.setAgents(live.agents.map(x => (x.id === u.id ? u : x)))" />
      <AgentTokenModal v-model:open="tokenOpen" :token="token" :org-name="org.current?.name ?? ''" :title="token ? 'New agent token' : 'Add an agent'" />
    </template>
  </UDashboardPanel>
</template>
