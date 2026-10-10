<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { TableColumn } from '@nuxt/ui'
import { api } from '@/api/client'
import type { MemberDto, OrgRole } from '@/api/types'
import { dateTime } from '@/lib/format'
import { useAuthStore } from '@/stores/auth'
import { useOrgStore } from '@/stores/org'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import AgentTokenModal from '@/components/org/AgentTokenModal.vue'
import DataList from '@/components/DataList.vue'
import { useHighlight } from '@/composables/useHighlight'

const auth = useAuthStore()
const org = useOrgStore()
const live = useLiveStore()
const router = useRouter()
const notify = useNotify()
const confirm = useConfirm()

// ---------- general ----------
const name = ref(org.current?.name ?? '')
watch(() => org.current?.name, n => { name.value = n ?? '' })
const renaming = ref(false)
async function rename() {
  if (!name.value.trim() || name.value.trim() === org.current?.name) return
  renaming.value = true
  try {
    org.upsert(await api.org.rename(name.value.trim()))
    notify.success('Organization renamed')
  } catch (e) { notify.error(e, 'Rename failed') } finally { renaming.value = false }
}

// ---------- agent token ----------
const token = ref<string | null>(null)
const tokenOpen = ref(false)
const regenerating = ref(false)
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

// ---------- members ----------
const members = ref<MemberDto[]>([])
const loading = ref(true)
const busyId = ref<string | null>(null)
const ROLES: OrgRole[] = ['Member', 'Admin', 'Owner']
const assignable = computed(() => ROLES.filter(r => r !== 'Owner' || org.isOwner))
const roleItems = computed(() => assignable.value.map(r => ({ label: r, value: r })))
const owners = computed(() => members.value.filter(m => m.role === 'Owner').length)

const isSelf = (m: MemberDto) => m.userName.toLowerCase() === auth.user?.userName.toLowerCase()
/** Admins manage members and admins; only owners touch owners. */
const canManage = (m: MemberDto) => !isSelf(m) && org.isAdmin && (m.role !== 'Owner' || org.isOwner)

type MemberRow = MemberDto & { id: string }
const rows = computed<MemberRow[]>(() => members.value.map(m => ({ ...m, id: m.userId })))
const hl = useHighlight()
const columns: TableColumn<MemberRow>[] = [
  { accessorKey: 'displayName', header: 'Member' },
  { accessorKey: 'role', header: 'Role' },
  { id: 'joined', header: 'Joined' },
  { id: 'actions', header: '' },
]

async function loadMembers() {
  try { members.value = await api.org.members() } catch (e) { notify.error(e, 'Could not load members') } finally { loading.value = false }
}

async function changeRole(m: MemberDto, role: OrgRole) {
  if (role === m.role) return
  busyId.value = m.userId
  try {
    const updated = await api.org.setRole(m.userId, role)
    members.value = members.value.map(x => (x.userId === m.userId ? updated : x))
    void hl.flash(updated.userId)
    notify.success(`${m.displayName} is now ${role}`)
  } catch (e) { notify.error(e, 'Role change failed'); members.value = [...members.value] } finally { busyId.value = null }
}

async function remove(m: MemberDto) {
  if (!await confirm({ title: 'Remove member', message: `Remove ${m.displayName} from ${org.current?.name}?`, confirmLabel: 'Remove', danger: true })) return
  busyId.value = m.userId
  try {
    await api.org.removeMember(m.userId)
    members.value = members.value.filter(x => x.userId !== m.userId)
    notify.success(`Removed ${m.displayName}`)
  } catch (e) { notify.error(e, 'Remove failed') } finally { busyId.value = null }
}

async function leave() {
  const me = members.value.find(isSelf)
  if (!me) return
  if (me.role === 'Owner' && owners.value <= 1) {
    notify.error(new Error('Make someone else an owner first — an organization always keeps at least one owner.'), 'You are the last owner')
    return
  }
  if (!await confirm({ title: 'Leave organization', message: `Leave ${org.current?.name}? You lose access until someone adds you again.`, confirmLabel: 'Leave', danger: true })) return
  try {
    await api.org.removeMember(me.userId)
    notify.success(`You left ${org.current?.name}`)
    await org.load()
    await live.joinOrg(org.currentId)
    await router.push(org.orgs.length ? '/' : { name: 'onboarding' })
  } catch (e) { notify.error(e, 'Could not leave') }
}

// ---------- add member ----------
const addOpen = ref(false)
const add = reactive({ email: '', role: 'Member' as OrgRole })
const adding = ref(false)
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
async function addMember() {
  if (!EMAIL.test(add.email.trim())) return
  adding.value = true
  try {
    const m = await api.org.addMember(add.email.trim(), add.role)
    members.value = [...members.value.filter(x => x.userId !== m.userId), m]
    void hl.flash(m.userId)
    notify.success(`Added ${m.email ?? m.userName}`, m.canSignInWithGoogle ? 'They can sign in with Google using that address.' : undefined)
    addOpen.value = false
    add.email = ''
    add.role = 'Member'
  } catch (e) { notify.error(e, 'Could not add member') } finally { adding.value = false }
}

onMounted(loadMembers)
</script>

<template>
  <UDashboardPanel id="settings">
    <template #header>
      <UDashboardNavbar title="Organization settings" icon="i-lucide-settings">
        <template #trailing>
          <UBadge v-if="org.role" :label="`You: ${org.role}`" color="neutral" variant="subtle" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div class="grid gap-6 lg:grid-cols-2">
        <UCard>
          <template #header>
            <h2 class="font-semibold text-highlighted">General</h2>
            <p class="text-sm text-muted">Slug <code>{{ org.current?.slug }}</code> · created {{ dateTime(org.current?.createdAt) }}</p>
          </template>
          <form class="flex items-end gap-2" @submit.prevent="rename">
            <UFormField label="Name" class="flex-1">
              <UInput v-model="name" class="w-full" :disabled="!org.isAdmin" />
            </UFormField>
            <UButton v-if="org.isAdmin" type="submit" label="Save" :loading="renaming" :disabled="!name.trim() || name.trim() === org.current?.name" />
          </form>
        </UCard>

        <UCard>
          <template #header>
            <h2 class="font-semibold text-highlighted">Agent token</h2>
            <p class="text-sm text-muted">Daemons register with this organization's token. It is only shown when created.</p>
          </template>
          <div class="flex flex-wrap gap-2">
            <UButton v-if="org.isAdmin" icon="i-lucide-refresh-cw" label="Regenerate token" color="warning" variant="outline" :loading="regenerating" @click="regenerate" />
            <UButton icon="i-lucide-terminal" label="Installation help" color="neutral" variant="outline" @click="token = null; tokenOpen = true" />
          </div>
          <p v-if="!org.isAdmin" class="mt-3 text-xs text-muted">Only admins can regenerate the token.</p>
        </UCard>
      </div>

      <UCard :ui="{ body: 'p-0 sm:p-0', header: 'sm:px-4 px-3 py-3' }">
        <template #header>
          <div class="flex flex-wrap items-center gap-2">
            <h2 class="flex-1 font-semibold text-highlighted">Members <span class="text-muted">({{ members.length }})</span></h2>
            <UButton icon="i-lucide-log-out" label="Leave organization" color="neutral" variant="ghost" size="sm" @click="leave" />
            <UButton v-if="org.isAdmin" icon="i-lucide-user-plus" label="Add member" size="sm" @click="addOpen = true" />
          </div>
        </template>
        <DataList :data="rows" :columns="columns" :loading="loading" :highlight-id="hl.id.value">
          <template #card="{ item: m }">
            <div class="flex items-center gap-2">
              <UAvatar :alt="m.displayName" size="sm" />
              <div class="min-w-0 flex-1">
                <div class="truncate font-medium text-highlighted">
                  {{ m.displayName }} <UBadge v-if="isSelf(m)" label="You" size="sm" color="primary" variant="subtle" />
                </div>
                <div class="truncate text-xs text-muted" :title="m.email ?? m.userName">{{ m.email ?? m.userName }}</div>
              </div>
              <USelect
                v-if="canManage(m)" :model-value="m.role" :items="roleItems" size="xs" class="w-28 shrink-0"
                :loading="busyId === m.userId" @update:model-value="(r: OrgRole) => changeRole(m, r)"
              />
              <UBadge v-else :label="m.role" :color="m.role === 'Owner' ? 'primary' : m.role === 'Admin' ? 'info' : 'neutral'" variant="subtle" class="shrink-0" />
              <UButton
                v-if="canManage(m)" icon="i-lucide-user-minus" size="xs" color="error" variant="ghost" class="shrink-0"
                aria-label="Remove member" :loading="busyId === m.userId" @click="remove(m)"
              />
            </div>
          </template>
          <template #displayName-cell="{ row }">
            <div class="flex items-center gap-2">
              <UAvatar :alt="row.original.displayName" size="sm" />
              <div class="min-w-0">
                <div class="truncate font-medium text-highlighted">
                  {{ row.original.displayName }}
                  <UBadge v-if="isSelf(row.original)" label="You" size="sm" color="primary" variant="subtle" class="ml-1" />
                </div>
                <div class="truncate text-xs text-muted">
                  {{ row.original.email ?? row.original.userName }}
                  <UIcon v-if="row.original.canSignInWithGoogle" name="i-simple-icons-google" class="ml-1 size-3 align-[-1px]" />
                </div>
              </div>
            </div>
          </template>
          <template #role-cell="{ row }">
            <USelect
              v-if="canManage(row.original)" :model-value="row.original.role" :items="roleItems" size="sm" class="w-32"
              :loading="busyId === row.original.userId" @update:model-value="(r: OrgRole) => changeRole(row.original, r)"
            />
            <UBadge v-else :label="row.original.role" :color="row.original.role === 'Owner' ? 'primary' : row.original.role === 'Admin' ? 'info' : 'neutral'" variant="subtle" />
          </template>
          <template #joined-cell="{ row }"><span class="text-xs text-muted">{{ dateTime(row.original.joinedAt) }}</span></template>
          <template #actions-cell="{ row }">
            <div class="flex justify-end">
              <UButton
                v-if="canManage(row.original)" icon="i-lucide-user-minus" size="xs" color="error" variant="ghost"
                aria-label="Remove member" :loading="busyId === row.original.userId" @click="remove(row.original)"
              />
            </div>
          </template>
        </DataList>
      </UCard>

      <UModal v-model:open="addOpen" title="Add member" description="Existing users are added immediately. An unknown e-mail becomes an invitation: that address can then sign in with Google.">
        <template #body>
          <form id="add-member" class="grid gap-4 sm:grid-cols-[1fr_9rem]" @submit.prevent="addMember">
            <UFormField label="E-mail" required :error="add.email && !EMAIL.test(add.email.trim()) ? 'Not an e-mail address' : undefined">
              <UInput v-model="add.email" type="email" class="w-full" placeholder="jane@contoso.com" autofocus />
            </UFormField>
            <UFormField label="Role">
              <USelect v-model="add.role" :items="roleItems" class="w-full" />
            </UFormField>
          </form>
        </template>
        <template #footer>
          <div class="flex w-full justify-end gap-2">
            <UButton color="neutral" variant="outline" label="Cancel" @click="addOpen = false" />
            <UButton type="submit" form="add-member" label="Add" :loading="adding" :disabled="!EMAIL.test(add.email.trim())" />
          </div>
        </template>
      </UModal>

      <AgentTokenModal v-model:open="tokenOpen" :token="token" :org-name="org.current?.name ?? ''" :title="token ? 'New agent token' : 'Add an agent'" />
    </template>
  </UDashboardPanel>
</template>
