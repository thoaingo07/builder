<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import type { DropdownMenuItem, NavigationMenuItem } from '@nuxt/ui'
import { useAuthStore } from '@/stores/auth'
import { useLiveStore } from '@/stores/live'

const auth = useAuthStore()
const live = useLiveStore()
const router = useRouter()

const nav: NavigationMenuItem[][] = [
  [
    { label: 'Dashboard', icon: 'i-lucide-layout-dashboard', to: '/', exact: true },
    { label: 'Builds', icon: 'i-lucide-hammer', to: '/builds' },
    { label: 'Pipelines', icon: 'i-lucide-workflow', to: '/pipelines' },
    { label: 'Agents', icon: 'i-lucide-server', to: '/agents' },
  ],
  [
    { label: 'Environments', icon: 'i-lucide-cloud', to: '/environments' },
    { label: 'Deployments', icon: 'i-lucide-rocket', to: '/deployments' },
    { label: 'Connections', icon: 'i-lucide-git-branch', to: '/connections' },
    { label: 'Cleanup', icon: 'i-lucide-brush-cleaning', to: '/cleanup' },
  ],
]

const userMenu = computed<DropdownMenuItem[][]>(() => [
  [{ type: 'label', label: auth.user?.displayName ?? '', description: auth.user?.isAdmin ? 'Administrator' : 'User' }],
  [{ label: 'Sign out', icon: 'i-lucide-log-out', onSelect: logout }],
])

const liveState = computed(() => ({
  connected: { color: 'success' as const, label: 'Live' },
  connecting: { color: 'warning' as const, label: 'Connecting' },
  reconnecting: { color: 'warning' as const, label: 'Reconnecting' },
  disconnected: { color: 'error' as const, label: 'Offline' },
}[live.state]))

async function logout() {
  await live.stop()
  await auth.logout()
  await router.push({ name: 'login' })
}
</script>

<template>
  <UDashboardGroup unit="rem">
    <UDashboardSidebar collapsible resizable :default-size="15" :min-size="12" :max-size="20" :ui="{ footer: 'border-t border-default' }">
      <template #header="{ collapsed }">
        <RouterLink to="/" class="flex items-center gap-2 font-semibold">
          <span class="flex size-7 items-center justify-center rounded-md bg-primary text-inverted">
            <UIcon name="i-lucide-blocks" class="size-4" />
          </span>
          <span v-if="!collapsed" class="text-highlighted">Builder</span>
        </RouterLink>
      </template>

      <template #default="{ collapsed }">
        <UNavigationMenu :collapsed="collapsed" :items="nav[0]" orientation="vertical" tooltip />
        <USeparator />
        <UNavigationMenu :collapsed="collapsed" :items="nav[1]" orientation="vertical" tooltip />
      </template>

      <template #footer="{ collapsed }">
        <div class="flex w-full flex-col gap-2">
          <div class="flex items-center gap-2 px-1 text-xs text-muted">
            <UChip :color="liveState.color" standalone inset size="md" />
            <span v-if="!collapsed">{{ liveState.label }}</span>
            <span class="flex-1" />
            <UColorModeButton v-if="!collapsed" size="xs" />
          </div>
          <UDropdownMenu :items="userMenu" :content="{ align: 'center', collisionPadding: 12 }" :ui="{ content: collapsed ? 'w-48' : 'w-(--reka-dropdown-menu-trigger-width)' }">
            <UButton
              :label="collapsed ? undefined : auth.user?.displayName" icon="i-lucide-circle-user-round"
              color="neutral" variant="ghost" block :square="collapsed" trailing-icon="i-lucide-chevrons-up-down"
              class="data-[state=open]:bg-elevated" :ui="{ trailingIcon: 'text-dimmed' }"
            />
          </UDropdownMenu>
        </div>
      </template>
    </UDashboardSidebar>

    <RouterView />
  </UDashboardGroup>
</template>
