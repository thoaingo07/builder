import { ApiError } from '@/api/client'

/** Toast helpers on top of Nuxt UI's useToast; must be called from setup. */
export function useNotify() {
  const toast = useToast()

  function error(e: unknown, fallback = 'Something went wrong') {
    if (e instanceof ApiError) {
      if (e.status === 401) return
      toast.add({ title: e.problem.title ?? fallback, description: e.problem.detail, color: 'error', icon: 'i-lucide-circle-alert', duration: 8000 })
    } else {
      toast.add({ title: fallback, description: e instanceof Error ? e.message : undefined, color: 'error', icon: 'i-lucide-circle-alert', duration: 8000 })
    }
  }

  function success(title: string, description?: string) {
    toast.add({ title, description, color: 'success', icon: 'i-lucide-circle-check' })
  }

  function info(title: string, description?: string) {
    toast.add({ title, description, color: 'info', icon: 'i-lucide-info' })
  }

  return { error, success, info }
}
