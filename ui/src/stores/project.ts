import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { api } from '@/api/client'
import type { ProjectDto } from '@/api/types'
import { useOrgStore } from './org'

const key = (orgId: string) => `builder.project.${orgId}`
function readStored(orgId: string): string | null {
  try { return localStorage.getItem(key(orgId)) } catch { return null }
}
function writeStored(orgId: string, id: string | null) {
  try {
    if (id) localStorage.setItem(key(orgId), id)
    else localStorage.removeItem(key(orgId))
  } catch { /* private mode: not remembered */ }
}

/**
 * Projects of the current organization and the selected one (null = "All projects").
 * List pages pass `query` as `?project=`; the selection is remembered per organization.
 */
export const useProjectStore = defineStore('project', () => {
  const org = useOrgStore()
  const projects = ref<ProjectDto[]>([])
  const currentId = ref<string | null>(null)
  const loaded = ref(false)
  let loadedFor: string | null = null

  const current = computed(() => projects.value.find(p => p.id === currentId.value) ?? null)
  /** value for `?project=` (undefined = all projects) */
  const query = computed(() => currentId.value ?? undefined)
  const byId = computed(() => new Map(projects.value.map(p => [p.id, p])))
  const nameOf = (id: string | null | undefined) => (id ? byId.value.get(id)?.name ?? 'Unknown project' : 'Shared')

  async function load() {
    const orgId = org.currentId
    if (!orgId) { projects.value = []; currentId.value = null; loaded.value = false; return }
    try {
      projects.value = await api.projects.list()
    } catch {
      projects.value = []
    }
    loadedFor = orgId
    loaded.value = true
    const stored = readStored(orgId)
    currentId.value = projects.value.some(p => p.id === stored) ? stored : null
  }

  async function ensureLoaded() {
    if (!loaded.value || loadedFor !== org.currentId) await load()
  }

  function select(id: string | null) {
    currentId.value = id
    if (org.currentId) writeStored(org.currentId, id)
  }

  function upsert(p: ProjectDto) {
    projects.value = projects.value.some(x => x.id === p.id)
      ? projects.value.map(x => (x.id === p.id ? p : x))
      : [...projects.value, p].sort((a, b) => a.name.localeCompare(b.name))
  }

  function removeLocal(id: string) {
    projects.value = projects.value.filter(p => p.id !== id)
    if (currentId.value === id) select(null)
  }

  /** an event/item belongs to the current view (all projects, or the selected one) */
  const matches = (projectId: string | null | undefined) => !currentId.value || projectId === currentId.value

  // another organization: forget the old projects, the router guard reloads them
  watch(() => org.currentId, id => {
    if (id !== loadedFor) { loaded.value = false; projects.value = []; currentId.value = null }
  })

  return { projects, currentId, current, query, byId, nameOf, loaded, load, ensureLoaded, select, upsert, removeLocal, matches }
})
