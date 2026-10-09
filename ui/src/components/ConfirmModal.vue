<script setup lang="ts">
defineProps<{ title: string; message: string; confirmLabel?: string; danger?: boolean }>()
const emit = defineEmits<{ close: [boolean] }>()
</script>

<template>
  <UModal :title="title" :close="false" :ui="{ footer: 'justify-end' }" @update:open="v => !v && emit('close', false)">
    <template #body>
      <p class="whitespace-pre-line text-sm text-muted">{{ message }}</p>
    </template>
    <template #footer>
      <UButton color="neutral" variant="outline" label="Cancel" @click="emit('close', false)" />
      <UButton :color="danger ? 'error' : 'primary'" :label="confirmLabel ?? 'Confirm'" autofocus @click="emit('close', true)" />
    </template>
  </UModal>
</template>
