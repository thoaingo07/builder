<script setup lang="ts">
import { computed, ref } from 'vue'
import { api } from '@/api/client'
import type { JobDto } from '@/api/types'
import { dateTime } from '@/lib/format'
import { useAuthStore } from '@/stores/auth'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ buildId: string; job: JobDto }>()
const emit = defineEmits<{ decided: [JobDto] }>()

const auth = useAuthStore()
const notify = useNotify()
const comment = ref('')
const busy = ref<'approve' | 'reject' | null>(null)

const approval = computed(() => props.job.approval!)
const waiting = computed(() => props.job.status === 'WaitingApproval')
const canDecide = computed(() => {
  const list = approval.value.approvers
  return !list.length || list.some(a => a.toLowerCase() === auth.user?.userName.toLowerCase())
})

async function decide(approved: boolean) {
  busy.value = approved ? 'approve' : 'reject'
  try {
    const job = await api.builds.approve(props.buildId, props.job.id, approved, comment.value.trim() || undefined)
    notify.success(approved ? `Approved ${props.job.taskName}` : `Rejected ${props.job.taskName}`)
    comment.value = ''
    emit('decided', job)
  } catch (e) {
    notify.error(e, 'Approval failed')
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <UAlert
    v-if="waiting" color="warning" variant="subtle" icon="i-lucide-lock"
    :title="approval.message || 'Approval required'"
    :description="approval.approvers.length ? `Approvers: ${approval.approvers.join(', ')}` : 'Any signed-in user can approve.'"
  >
    <template #actions>
      <div class="flex w-full flex-col gap-2">
        <UTextarea v-model="comment" placeholder="Comment (optional)" :rows="2" autoresize class="w-full" :disabled="!canDecide" />
        <div class="flex gap-2">
          <UButton icon="i-lucide-check" label="Approve" color="success" :loading="busy === 'approve'" :disabled="!canDecide || !!busy" @click="decide(true)" />
          <UButton icon="i-lucide-x" label="Reject" color="error" variant="outline" :loading="busy === 'reject'" :disabled="!canDecide || !!busy" @click="decide(false)" />
          <span v-if="!canDecide" class="self-center text-xs text-muted">You are not an approver for this gate.</span>
        </div>
      </div>
    </template>
  </UAlert>
  <UAlert
    v-else-if="approval.decidedBy"
    :color="job.status === 'Failed' ? 'error' : 'success'" variant="subtle"
    :icon="job.status === 'Failed' ? 'i-lucide-shield-x' : 'i-lucide-shield-check'"
    :title="`${job.status === 'Failed' ? 'Rejected' : 'Approved'} by ${approval.decidedBy}`"
    :description="[dateTime(approval.decidedAt), approval.comment].filter(Boolean).join(' — ')"
  />
  <UAlert
    v-else color="neutral" variant="subtle" icon="i-lucide-lock"
    :title="approval.message || 'Approval gate'" description="Waits for approval once its dependencies succeed."
  />
</template>
