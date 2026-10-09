import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api, setCurrentOrg } from '@/api/client'
import type { OrgCreatedDto, OrgDto, OrgRole } from '@/api/types'

const STORAGE_KEY = 'builder.currentOrg'

function readStored(): string | null {
  try { return localStorage.getItem(STORAGE_KEY) } catch { return null }
}
function writeStored(id: string | null) {
  try {
    if (id) localStorage.setItem(STORAGE_KEY, id)
    else localStorage.removeItem(STORAGE_KEY)
  } catch { /* private mode: the choice just isn't remembered */ }
}

const RANK: Record<OrgRole, number> = { Member: 0, Admin: 1, Owner: 2 }

/** The user's organizations and the current one (sent as X-Org by the API client). */
export const useOrgStore = defineStore('org', () => {
  const orgs = ref<OrgDto[]>([])
  const currentId = ref<string | null>(null)
  const loaded = ref(false)

  const current = computed(() => orgs.value.find(o => o.id === currentId.value) ?? null)
  const role = computed<OrgRole | null>(() => current.value?.role ?? null)
  const atLeast = (r: OrgRole) => (role.value ? RANK[role.value] >= RANK[r] : false)
  const isAdmin = computed(() => atLeast('Admin'))
  const isOwner = computed(() => atLeast('Owner'))

  function select(id: string | null) {
    currentId.value = id
    setCurrentOrg(id)
    writeStored(id)
  }

  /** Picks the stored org if still valid, else the first one. */
  function pickDefault() {
    const stored = currentId.value ?? readStored()
    select(orgs.value.some(o => o.id === stored) ? stored : (orgs.value[0]?.id ?? null))
  }

  async function load() {
    const me = await api.me()
    orgs.value = me.orgs
    loaded.value = true
    pickDefault()
    return me
  }

  /** The stored org is gone or not ours: forget it and fall back to the first org. */
  function invalidate() {
    const bad = currentId.value
    orgs.value = orgs.value.filter(o => o.id !== bad)
    currentId.value = null
    writeStored(null)
    pickDefault()
    void load().catch(() => undefined)
  }

  async function create(name: string): Promise<OrgCreatedDto> {
    const created = await api.createOrg(name)
    orgs.value = [...orgs.value, created.org]
    select(created.org.id)
    return created
  }

  function upsert(org: OrgDto) {
    orgs.value = orgs.value.some(o => o.id === org.id) ? orgs.value.map(o => (o.id === org.id ? org : o)) : [...orgs.value, org]
  }

  function reset() {
    orgs.value = []
    loaded.value = false
    currentId.value = null
    setCurrentOrg(null)
  }

  return { orgs, currentId, current, role, isAdmin, isOwner, loaded, load, select, invalidate, create, upsert, reset }
})
