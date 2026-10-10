import { onScopeDispose, ref, type Ref } from 'vue'

const QUERY = '(max-width: 767px)'   // below Tailwind's md breakpoint
let shared: Ref<boolean> | null = null
let users = 0
let mql: MediaQueryList | null = null
const onChange = (e: MediaQueryListEvent) => { if (shared) shared.value = e.matches }

/** True below the md breakpoint (phones): lists render as cards, graphs default to lists. */
export function useIsMobile(): Ref<boolean> {
  if (!shared) {
    mql = window.matchMedia(QUERY)
    shared = ref(mql.matches)
    mql.addEventListener('change', onChange)
  }
  users++
  onScopeDispose(() => {
    if (--users === 0 && mql) {
      mql.removeEventListener('change', onChange)
      mql = null
      shared = null
    }
  })
  return shared
}
