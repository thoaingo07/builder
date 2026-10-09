import { useNotify } from './useNotify'

/** Copies text with a toast; falls back to a hidden textarea where the async clipboard API is unavailable (http). */
export function useCopy() {
  const notify = useNotify()
  return async (text: string, what = 'Copied') => {
    try {
      if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text)
      else {
        const ta = document.createElement('textarea')
        ta.value = text
        ta.style.position = 'fixed'
        ta.style.opacity = '0'
        document.body.appendChild(ta)
        ta.select()
        document.execCommand('copy')
        ta.remove()
      }
      notify.success(what)
    } catch (e) {
      notify.error(e, 'Copy failed')
    }
  }
}
