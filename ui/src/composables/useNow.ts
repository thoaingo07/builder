import { onBeforeUnmount, onMounted, ref } from 'vue'

/** A reactive "now" that ticks, for live durations and relative times. */
export function useNow(intervalMs = 1000) {
  const now = ref(Date.now())
  let timer: number | undefined
  onMounted(() => { timer = window.setInterval(() => { now.value = Date.now() }, intervalMs) })
  onBeforeUnmount(() => window.clearInterval(timer))
  return now
}
