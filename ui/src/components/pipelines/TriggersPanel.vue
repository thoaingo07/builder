<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api } from '@/api/client'
import type { PipelineTriggersDto } from '@/api/types'
import { shortSha } from '@/lib/format'
import { useNotify } from '@/composables/useNotify'
import TriggersView from './TriggersView.vue'

/** Read-only view of a runner's x-builder.triggers as the server last read them. */
const props = defineProps<{ pipelineId: string; defaultBranch: string; initial?: PipelineTriggersDto | null }>()
const emit = defineEmits<{ loaded: [PipelineTriggersDto] }>()
const notify = useNotify()

const data = ref<PipelineTriggersDto | null>(props.initial ?? null)
const loading = ref(false)
const refreshing = ref(false)

async function load() {
  loading.value = true
  try {
    data.value = await api.pipelines.triggers(props.pipelineId)
    emit('loaded', data.value)
  } catch (e) { notify.error(e, 'Could not load triggers') } finally { loading.value = false }
}

async function refresh() {
  refreshing.value = true
  try {
    data.value = await api.pipelines.refreshTriggers(props.pipelineId)
    emit('loaded', data.value)
    notify.success('Triggers re-read', `${props.defaultBranch} @ ${shortSha(data.value.commit)}`)
  } catch (e) { notify.error(e, 'Refresh failed') } finally { refreshing.value = false }
}

watch(() => props.pipelineId, () => { data.value = null; void load() })
onMounted(() => { if (!data.value) void load() })
</script>

<template>
  <div class="space-y-4">
    <div class="flex items-center gap-2 text-xs text-muted">
      <UIcon name="i-lucide-git-branch" />
      <span>read from <strong class="text-default">{{ defaultBranch }}</strong> @ <code>{{ shortSha(data?.commit) }}</code></span>
      <span class="flex-1" />
      <UButton icon="i-lucide-refresh-cw" label="Refresh" size="xs" color="neutral" variant="outline" :loading="refreshing" @click="refresh" />
    </div>

    <USkeleton v-if="loading && !data" class="h-32 w-full" />
    <template v-else-if="data">
      <UAlert v-if="data.error" color="error" variant="subtle" icon="i-lucide-circle-x" title="The runner file's triggers are invalid" :description="data.error" />

      <TriggersView :spec="data.triggers" :runs="data.schedules" :default-branch="defaultBranch" />

      <p class="text-xs text-muted">
        Triggers live in the runner file under <code>x-builder.triggers</code> and are changed in the repository; Builder re-reads them from {{ defaultBranch }} on push.
      </p>
    </template>
  </div>
</template>
