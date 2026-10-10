import { computed, type Ref } from 'vue'
import { useProjectStore } from '@/stores/project'

interface Scoped { id: string; name: string; projectId: string | null }
export interface ScopedSection<T> { key: 'project' | 'shared' | 'all'; title: string | null; hint: string | null; items: T[] }

/**
 * Splits project-or-shared items (connections, environments, secrets) for display:
 * a selected project → "This project" + "Shared with all projects"; all projects → one list (rows carry a scope badge).
 * `overrides(item)` is true for a project item whose name matches a shared one (the runner uses the project's).
 */
export function useScopedSections<T extends Scoped>(items: Ref<T[]>) {
  const project = useProjectStore()

  const sharedNames = computed(() => new Set(items.value.filter(i => i.projectId === null).map(i => i.name.toLowerCase())))
  const overrides = (i: T) => i.projectId !== null && sharedNames.value.has(i.name.toLowerCase())

  const sections = computed<ScopedSection<T>[]>(() => {
    const pid = project.currentId
    if (!pid) return [{ key: 'all', title: null, hint: null, items: items.value }]
    return [
      { key: 'project', title: `This project · ${project.current?.name ?? ''}`, hint: 'Used first by this project\'s runners.', items: items.value.filter(i => i.projectId === pid) },
      { key: 'shared', title: 'Shared with all projects', hint: 'Used when the project has no item with the same name.', items: items.value.filter(i => i.projectId === null) },
    ]
  })

  /** scope for a new item: the selected project, or shared under "All projects" */
  const defaultScope = computed<string | null>(() => project.currentId)

  return { sections, overrides, defaultScope, showScope: computed(() => !project.currentId) }
}
