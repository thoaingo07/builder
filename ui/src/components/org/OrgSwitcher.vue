<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import type { DropdownMenuItem } from '@nuxt/ui'
import type { OrgCreatedDto, OrgRole } from '@/api/types'
import { useOrgStore } from '@/stores/org'
import { useLiveStore } from '@/stores/live'
import { useProjectStore } from '@/stores/project'
import CreateOrgModal from './CreateOrgModal.vue'
import AgentTokenModal from './AgentTokenModal.vue'

defineProps<{ collapsed?: boolean }>()

const org = useOrgStore()
const live = useLiveStore()
const project = useProjectStore()
const router = useRouter()
const createOpen = ref(false)
const tokenOpen = ref(false)
const created = ref<OrgCreatedDto | null>(null)

const roleColor = (r: OrgRole) => (r === 'Owner' ? 'primary' : r === 'Admin' ? 'info' : 'neutral')
const roleOf = (item: unknown) => (item as { role: OrgRole }).role

async function switchTo(id: string) {
  if (id === org.currentId) return
  org.select(id)
  await live.joinOrg(id)
  await project.load()
  // detail pages belong to the previous organization
  if (Object.keys(router.currentRoute.value.params).length) await router.push('/')
}

const items = computed<DropdownMenuItem[][]>(() => [
  org.orgs.map(o => ({
    label: o.name,
    icon: o.id === org.currentId ? 'i-lucide-check' : 'i-lucide-building-2',
    slot: 'org' as const,
    role: o.role,
    onSelect: () => void switchTo(o.id),
  })),
  [
    { label: 'Organization settings', icon: 'i-lucide-settings', to: '/settings' },
    { label: 'Create organization…', icon: 'i-lucide-plus', onSelect: () => { createOpen.value = true } },
  ],
])

async function onCreated(c: OrgCreatedDto) {
  created.value = c
  tokenOpen.value = true
  await live.joinOrg(c.org.id)
  await project.load()
  await router.push('/')
}
</script>

<template>
  <UDropdownMenu :items="items" :content="{ align: 'start', collisionPadding: 12 }" :ui="{ content: 'min-w-60' }">
    <UButton
      color="neutral" variant="ghost" block :square="collapsed" class="data-[state=open]:bg-elevated"
      :trailing-icon="collapsed ? undefined : 'i-lucide-chevrons-up-down'" :ui="{ trailingIcon: 'text-dimmed' }"
      :aria-label="`Organization: ${org.current?.name ?? 'none'}`"
    >
      <span class="flex size-7 shrink-0 items-center justify-center rounded-md bg-primary text-inverted">
        <UIcon name="i-lucide-blocks" class="size-4" />
      </span>
      <span v-if="!collapsed" class="min-w-0 flex-1 text-left">
        <span class="block truncate text-sm font-semibold text-highlighted">{{ org.current?.name ?? 'Builder' }}</span>
        <span class="block truncate text-xs font-normal text-muted">{{ org.current?.role ?? '' }}</span>
      </span>
    </UButton>
    <template #org-trailing="{ item }">
      <UBadge :label="roleOf(item)" :color="roleColor(roleOf(item))" variant="subtle" size="sm" />
    </template>
  </UDropdownMenu>

  <CreateOrgModal v-model:open="createOpen" @created="onCreated" />
  <AgentTokenModal
    v-model:open="tokenOpen" :token="created?.agentToken ?? null" :org-name="created?.org.name ?? ''"
    title="Organization created — connect an agent"
  />
</template>
