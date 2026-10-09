import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, bff } from '@/api/client'
import type { UserDto } from '@/api/types'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<UserDto | null>(null)
  const checked = ref(false)

  async function load(): Promise<UserDto | null> {
    try {
      user.value = await bff.user()
    } catch (e) {
      if (!(e instanceof ApiError && e.status === 401)) console.warn('user lookup failed', e)
      user.value = null
    } finally {
      checked.value = true
    }
    return user.value
  }

  async function login(userName: string, password: string) {
    user.value = await bff.login(userName, password)
    checked.value = true
  }

  async function logout() {
    try { await bff.logout() } finally { user.value = null }
  }

  function clear() { user.value = null }

  return { user, checked, load, login, logout, clear }
})
