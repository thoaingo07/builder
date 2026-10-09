<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '@/api/client'
import type { BuildStatus, BuildSummaryDto, PipelineDto } from '@/api/types'
import { upsert } from '@/lib/collections'
import { useLiveStore } from '@/stores/live'
import { useNotify } from '@/composables/useNotify'
import BuildsTable from '@/components/BuildsTable.vue'

const route = useRoute()
const router = useRouter()
const live = useLiveStore()
const notify = useNotify()

const ALL = 'all'
const STATUSES: BuildStatus[] = ['Planning', 'Running', 'Canceling', 'Succeeded', 'Failed', 'Canceled']

const pipelines = ref<PipelineDto[]>([])
const builds = ref<BuildSummaryDto[]>([])
const loading = ref(true)
const pipelineId = ref<string>(typeof route.query.pipelineId === 'string' ? route.query.pipelineId : ALL)
const status = ref<string>(typeof route.query.status === 'string' ? route.query.status : ALL)
const take = ref(50)

const pipelineItems = computed(() => [{ label: 'All runners', value: ALL }, ...pipelines.value.map(p => ({ label: p.name, value: p.id }))])
const statusItems = [{ label: 'Any status', value: ALL }, ...STATUSES.map(s => ({ label: s, value: s }))]

async function load() {
  loading.value = true
  try {
    builds.value = await api.builds.list({
      pipelineId: pipelineId.value === ALL ? null : pipelineId.value,
      status: status.value === ALL ? null : (status.value as BuildStatus),
      take: take.value,
    })
  } catch (e) {
    notify.error(e, 'Could not load builds')
  } finally {
    loading.value = false
  }
}

watch([pipelineId, status, take], () => {
  void router.replace({ query: { ...(pipelineId.value !== ALL && { pipelineId: pipelineId.value }), ...(status.value !== ALL && { status: status.value }) } })
  void load()
})

function matches(b: BuildSummaryDto) {
  return (pipelineId.value === ALL || b.pipelineId === pipelineId.value) && (status.value === ALL || b.status === status.value)
}

let off: (() => void) | undefined
onMounted(async () => {
  void load()
  off = live.onBuild(b => {
    if (matches(b)) builds.value = upsert(builds.value, b)
    else builds.value = builds.value.filter(x => x.id !== b.id)
  })
  try { pipelines.value = await api.pipelines.list() } catch { /* filter falls back to "all" */ }
})
onBeforeUnmount(() => off?.())
</script>

<template>
  <UDashboardPanel id="builds">
    <template #header>
      <UDashboardNavbar title="Builds" icon="i-lucide-hammer">
        <template #right>
          <UButton icon="i-lucide-refresh-cw" color="neutral" variant="ghost" aria-label="Refresh" :loading="loading" @click="load" />
          <UButton icon="i-lucide-play" label="Run a runner" to="/pipelines" />
        </template>
      </UDashboardNavbar>
      <UDashboardToolbar>
        <template #left>
          <USelect v-model="pipelineId" :items="pipelineItems" class="w-56" icon="i-lucide-workflow" />
          <USelect v-model="status" :items="statusItems" class="w-44" icon="i-lucide-filter" />
        </template>
      </UDashboardToolbar>
    </template>

    <template #body>
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <BuildsTable :builds="builds" :loading="loading" empty="No builds match these filters" />
      </UCard>
      <div v-if="builds.length >= take" class="flex justify-center">
        <UButton label="Load more" color="neutral" variant="outline" @click="take += 50" />
      </div>
    </template>
  </UDashboardPanel>
</template>
