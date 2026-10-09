<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { FormError, FormSubmitEvent } from '@nuxt/ui'
import { ApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { useLiveStore } from '@/stores/live'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const state = reactive({ userName: '', password: '' })
const busy = ref(false)
const googleEnabled = ref(false)
const passwordEnabled = ref(true)

// errors the BFF reports back after a Google round trip
const googleErrors: Record<string, string> = {
  not_allowed: "This Google account isn't allowed to use Builder. Ask an administrator to add your e-mail.",
  google_failed: 'Google sign-in did not complete. Please try again.',
}
const error = ref<string | null>(typeof route.query.error === 'string' ? googleErrors[route.query.error] ?? null : null)

const redirectTarget = () =>
  typeof route.query.redirect === 'string' && route.query.redirect.startsWith('/') ? route.query.redirect : '/'

onMounted(async () => {
  try {
    const res = await fetch('/bff/providers', { credentials: 'same-origin' })
    if (res.ok) {
      const providers = await res.json()
      googleEnabled.value = providers.google === true
      passwordEnabled.value = providers.password !== false
    }
  } catch {
    // password sign-in still works
  }
})

function signInWithGoogle() {
  window.location.href = '/bff/login/google?returnUrl=' + encodeURIComponent(redirectTarget())
}

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
    await router.replace(redirectTarget())
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

      <UForm v-if="passwordEnabled" :state="state" :validate="validate" class="space-y-4" @submit="submit">
        <UFormField label="User name" name="userName">
          <UInput v-model="state.userName" autocomplete="username" autofocus class="w-full" />
        </UFormField>
        <UFormField label="Password" name="password">
          <UInput v-model="state.password" type="password" autocomplete="current-password" class="w-full" />
        </UFormField>
        <UAlert v-if="error" color="error" variant="subtle" icon="i-lucide-circle-alert" :title="error" />
        <UButton type="submit" block :loading="busy" label="Sign in" />
      </UForm>

      <UAlert v-if="!passwordEnabled && error" color="error" variant="subtle" icon="i-lucide-circle-alert" :title="error" class="mb-4" />
      <template v-if="googleEnabled">
        <USeparator v-if="passwordEnabled" label="or" class="my-4" />
        <UButton block color="neutral" variant="outline" icon="i-simple-icons-google" label="Sign in with Google"
          @click="signInWithGoogle" />
      </template>
    </UCard>
  </div>
</template>
