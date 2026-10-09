<script setup lang="ts">
import { reactive, ref } from 'vue'
import type { FormError } from '@nuxt/ui'
import { api } from '@/api/client'
import { useNotify } from '@/composables/useNotify'

const emit = defineEmits<{ close: [] }>()
const notify = useNotify()

const MIN = 12
const state = reactive({ current: '', next: '', confirm: '' })
const busy = ref(false)
const error = ref<string | null>(null)

function validate(s: typeof state): FormError[] {
  const errors: FormError[] = []
  if (!s.current) errors.push({ name: 'current', message: 'Required' })
  if (s.next.length < MIN) errors.push({ name: 'next', message: `At least ${MIN} characters` })
  if (s.next && s.next === s.current) errors.push({ name: 'next', message: 'Must differ from the current password' })
  if (s.confirm !== s.next) errors.push({ name: 'confirm', message: 'Does not match' })
  return errors
}

async function submit() {
  busy.value = true
  error.value = null
  try {
    await api.changePassword(state.current, state.next)
    notify.success('Password changed')
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UModal title="Change password" :ui="{ footer: 'justify-end' }">
    <template #body>
      <UForm id="change-password" :state="state" :validate="validate" class="space-y-4" @submit="submit">
        <UFormField label="Current password" name="current">
          <UInput v-model="state.current" type="password" autocomplete="current-password" class="w-full" autofocus />
        </UFormField>
        <UFormField label="New password" name="next" :hint="`${MIN}+ characters`">
          <UInput v-model="state.next" type="password" autocomplete="new-password" class="w-full" />
        </UFormField>
        <UFormField label="Repeat new password" name="confirm">
          <UInput v-model="state.confirm" type="password" autocomplete="new-password" class="w-full" />
        </UFormField>
        <UAlert v-if="error" color="error" variant="subtle" icon="i-lucide-circle-alert" :title="error" />
      </UForm>
    </template>
    <template #footer>
      <UButton color="neutral" variant="ghost" label="Cancel" @click="emit('close')" />
      <UButton type="submit" form="change-password" :loading="busy" label="Change password" />
    </template>
  </UModal>
</template>
