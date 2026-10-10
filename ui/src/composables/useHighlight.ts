import { nextTick, ref } from 'vue'

/**
 * After a create/update: scroll the item into view and flash it briefly (long lists on phones).
 * Items mark themselves with `data-hl="<id>"` (cards) or class `hl-id-<id>` (table rows); DataList does both.
 */
export function useHighlight(ms = 2400) {
  const id = ref<string | null>(null)
  let timer: number | undefined
  async function flash(itemId: string) {
    id.value = itemId
    window.clearTimeout(timer)
    timer = window.setTimeout(() => { if (id.value === itemId) id.value = null }, ms)
    await nextTick()
    await new Promise(r => requestAnimationFrame(r))
    document.querySelector(`[data-hl="${CSS.escape(itemId)}"], .hl-id-${CSS.escape(itemId)}`)?.scrollIntoView({ block: 'center', behavior: 'smooth' })
  }
  return { id, flash }
}
