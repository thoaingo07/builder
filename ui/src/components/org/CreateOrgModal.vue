<script setup lang="ts">
import { ref, watch } from 'vue'
import type { OrgCreatedDto } from '@/api/types'
import { useOrgStore } from '@/stores/org'
import { useNotify } from '@/composables/useNotify'

const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ created: [OrgCreatedDto] }>()
const org = useOrgStore()
const notify = useNotify()
const name = ref('')
const busy = ref(false)

watch(open, o => { if (o) name.value = '' })

async function submit() {
  if (!name.value.trim()) return
  busy.value = true
  try {
    const created = await org.create(name.value.trim())
    open.value = false
    emit('created', created)
  } catch (e) {
    notify.error(e, 'Could not create organization')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" title="Create organization" description="Repositories, runners, agents and secrets belong to one organization. You become its owner.">
    <template #body>
      <form id="create-org" @submit.prevent="submit">
        <UFormField label="Name" required>
          <UInput v-model="name" class="w-full" placeholder="Contoso" autofocus />
        </UFormField>
      </form>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="create-org" label="Create" :loading="busy" :disabled="!name.trim()" />
      </div>
    </template>
  </UModal>
</template>
