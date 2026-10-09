<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import type { OrgCreatedDto } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useOrgStore } from '@/stores/org'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import AgentTokenModal from '@/components/org/AgentTokenModal.vue'

const auth = useAuthStore()
const org = useOrgStore()
const live = useLiveStore()
const router = useRouter()
const notify = useNotify()

const name = ref('')
const busy = ref(false)
const created = ref<OrgCreatedDto | null>(null)
const tokenOpen = ref(false)

async function submit() {
  if (!name.value.trim()) return
  busy.value = true
  try {
    created.value = await org.create(name.value.trim())
    tokenOpen.value = true
  } catch (e) {
    notify.error(e, 'Could not create organization')
  } finally {
    busy.value = false
  }
}

// leave onboarding once the token has been seen
async function done(open: boolean) {
  tokenOpen.value = open
  if (open || !created.value) return
  await live.joinOrg(created.value.org.id)
  await router.replace('/')
}

async function logout() {
  await live.stop()
  await auth.logout()
  org.reset()
  await router.push({ name: 'login' })
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-muted p-4">
    <UCard class="w-full max-w-md">
      <template #header>
        <div class="flex items-center gap-3">
          <span class="flex size-9 items-center justify-center rounded-lg bg-primary text-inverted">
            <UIcon name="i-lucide-building-2" class="size-5" />
          </span>
          <div>
            <h1 class="text-lg font-semibold text-highlighted">Create your first organization</h1>
            <p class="text-sm text-muted">Welcome, {{ auth.user?.displayName }}.</p>
          </div>
        </div>
      </template>

      <form class="space-y-4" @submit.prevent="submit">
        <p class="text-sm text-muted">
          An organization holds your git connections, repositories, runners, agents and secrets.
          You can invite teammates afterwards, or ask an existing organization's admin to add you instead.
        </p>
        <UFormField label="Organization name" required>
          <UInput v-model="name" class="w-full" placeholder="Contoso" autofocus />
        </UFormField>
        <UButton type="submit" block label="Create organization" :loading="busy" :disabled="!name.trim()" />
      </form>

      <template #footer>
        <div class="flex items-center justify-between text-sm">
          <span class="text-muted">Signed in as {{ auth.user?.userName }}</span>
          <UButton label="Sign out" variant="link" color="neutral" size="sm" @click="logout" />
        </div>
      </template>
    </UCard>

    <AgentTokenModal
      :open="tokenOpen" :token="created?.agentToken ?? null" :org-name="created?.org.name ?? ''"
      title="Organization created — connect an agent" @update:open="done"
    />
  </div>
</template>
