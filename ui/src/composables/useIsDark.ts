import { onBeforeUnmount, ref } from 'vue'

/** Tracks Nuxt UI's color mode (the `dark` class on <html>). */
export function useIsDark() {
  const root = document.documentElement
  const isDark = ref(root.classList.contains('dark'))
  const observer = new MutationObserver(() => { isDark.value = root.classList.contains('dark') })
  observer.observe(root, { attributes: true, attributeFilter: ['class'] })
  onBeforeUnmount(() => observer.disconnect())
  return isDark
}
