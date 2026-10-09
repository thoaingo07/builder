<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { FormError, FormSubmitEvent } from '@nuxt/ui'
import { ApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { useLiveStore } from '@/stores/live'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const state = reactive({ userName: '', password: '' })
const error = ref<string | null>(null)
const busy = ref(false)

function validate(s: typeof state): FormError[] {
  const errors: FormError[] = []
  if (!s.userName.trim()) errors.push({ name: 'userName', message: 'Required' })
  if (!s.password) errors.push({ name: 'password', message: 'Required' })
  return errors
}

async function submit(_e: FormSubmitEvent<typeof state>) {
  busy.value = true
  error.value = null
  try {
    await auth.login(state.userName.trim(), state.password)
    void useLiveStore().start()
    const redirect = typeof route.query.redirect === 'string' && route.query.redirect.startsWith('/') ? route.query.redirect : '/'
    await router.replace(redirect)
  } catch (e) {
    error.value = e instanceof ApiError && e.status === 401 ? 'Wrong user name or password.' : (e as Error).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-muted p-4">
    <UCard class="w-full max-w-sm">
      <template #header>
        <div class="flex items-center gap-3">
          <span class="flex size-9 items-center justify-center rounded-lg bg-primary text-inverted">
            <UIcon name="i-lucide-blocks" class="size-5" />
          </span>
          <div>
            <h1 class="text-lg font-semibold text-highlighted">Builder</h1>
            <p class="text-sm text-muted">Sign in to continue</p>
          </div>
        </div>
      </template>

      <UForm :state="state" :validate="validate" class="space-y-4" @submit="submit">
        <UFormField label="User name" name="userName">
          <UInput v-model="state.userName" autocomplete="username" autofocus class="w-full" />
        </UFormField>
        <UFormField label="Password" name="password">
          <UInput v-model="state.password" type="password" autocomplete="current-password" class="w-full" />
        </UFormField>
        <UAlert v-if="error" color="error" variant="subtle" icon="i-lucide-circle-alert" :title="error" />
        <UButton type="submit" block :loading="busy" label="Sign in" />
      </UForm>
    </UCard>
  </div>
</template>
