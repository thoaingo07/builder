<script setup lang="ts">
import { ref, watch } from 'vue'
import type { Document } from 'yaml'
import * as tf from '@/lib/taskfile'
import { debounce } from '@/lib/collections'
import { cronIsValid, describeCron, timeZones } from '@/lib/cron'
import KeyValueEditor from './KeyValueEditor.vue'

/** Edits x-builder.triggers (push, pull-request, schedule) through the yaml Document. */
const props = defineProps<{ triggers: tf.TriggersModel; defaultBranch: string }>()
const emit = defineEmits<{ mutate: [fn: (doc: Document) => void] }>()

type Filter = { enabled: boolean } & tf.FilterTriggerModel
const blankFilter = (): Filter => ({ enabled: false, branches: [], paths: [], vars: [] })
const toLocal = (f: tf.FilterTriggerModel | null): Filter =>
  f ? { enabled: true, branches: [...f.branches], paths: [...f.paths], vars: f.vars.map(v => ({ ...v })) } : blankFilter()

// local copy so typing isn't interrupted by the YAML round trip
const push = ref<Filter>(blankFilter())
const pr = ref<Filter>(blankFilter())
const schedules = ref<tf.ScheduleModel[]>([])
watch(() => props.triggers, t => {
  push.value = toLocal(t.push)
  pr.value = toLocal(t.pullRequest)
  schedules.value = t.schedules.map(s => ({ ...s, vars: s.vars.map(v => ({ ...v })) }))
}, { immediate: true, deep: true })

const zones = timeZones()

const savePush = debounce(() => {
  const v = push.value
  emit('mutate', d => tf.setFilterTrigger(d, 'push', v.enabled ? v : null))
}, 350)
const savePr = debounce(() => {
  const v = pr.value
  emit('mutate', d => tf.setFilterTrigger(d, 'pull-request', v.enabled ? v : null))
}, 350)
const saveSchedules = debounce(() => {
  // invalid cron expressions are still written so the server reports them; the preview flags them here
  const list = schedules.value.filter(s => s.cron.trim())
  emit('mutate', d => tf.setSchedules(d, list))
}, 450)

function addSchedule() {
  schedules.value.push({ cron: '0 3 * * *', branch: '', timeZone: 'UTC', vars: [] })
  saveSchedules()
}
function removeSchedule(i: number) {
  schedules.value.splice(i, 1)
  saveSchedules()
}

const filterSections = [
  { key: 'push', model: push, save: savePush, icon: 'i-lucide-git-commit-horizontal', title: 'Push', branchLabel: 'Branches', branchHelp: 'Empty = every branch. Wildcards like release/* work.' },
  { key: 'pr', model: pr, save: savePr, icon: 'i-lucide-git-pull-request', title: 'Pull request', branchLabel: 'Target branches', branchHelp: 'Pull requests into these branches; empty = any target.' },
] as const
</script>

<template>
  <div class="space-y-5">
    <UAlert
      v-if="triggers.error" color="error" variant="subtle" icon="i-lucide-circle-x" title="Triggers can't be edited here"
      :description="`${triggers.error} Fix it in the YAML tab.`"
    />

    <div v-for="sec in filterSections" :key="sec.key" class="space-y-3">
      <div class="flex items-center gap-2">
        <UIcon :name="sec.icon" class="text-muted" />
        <span class="flex-1 text-sm font-semibold">{{ sec.title }}</span>
        <USwitch v-model="sec.model.value.enabled" :disabled="!!triggers.error" :aria-label="`${sec.title} trigger`" @update:model-value="sec.save()" />
      </div>
      <template v-if="sec.model.value.enabled">
        <UFormField :label="sec.branchLabel" :help="sec.branchHelp">
          <UInputTags v-model="sec.model.value.branches" class="w-full font-mono" placeholder="all branches" @update:model-value="sec.save()" />
        </UFormField>
        <UFormField label="Paths" help="Only when these files change; prefix with ! to exclude, e.g. !docs/**.">
          <UInputTags v-model="sec.model.value.paths" class="w-full font-mono" placeholder="src/**" @update:model-value="sec.save()" />
        </UFormField>
        <UFormField label="Vars" help="Passed to the build like Run dialog variables.">
          <KeyValueEditor v-model="sec.model.value.vars" add-label="Add var" @update:model-value="sec.save()" />
        </UFormField>
      </template>
      <USeparator />
    </div>

    <div class="space-y-3">
      <div class="flex items-center gap-2">
        <UIcon name="i-lucide-clock" class="text-muted" />
        <span class="flex-1 text-sm font-semibold">Schedules</span>
        <UButton icon="i-lucide-plus" label="Add schedule" size="xs" color="neutral" variant="outline" :disabled="!!triggers.error" @click="addSchedule" />
      </div>
      <p v-if="!schedules.length" class="text-xs text-muted">No schedules. Times use cron syntax: minute hour day month weekday.</p>
      <div v-for="(s, i) in schedules" :key="i" class="space-y-2 rounded-md border border-default p-2">
        <div class="flex items-start gap-1">
          <UFormField class="flex-1" :error="s.cron.trim() && !cronIsValid(s.cron) ? 'Not a cron expression' : undefined">
            <UInput v-model="s.cron" size="sm" class="w-full font-mono" placeholder="0 3 * * *" @update:model-value="saveSchedules" />
          </UFormField>
          <UButton icon="i-lucide-x" size="xs" color="neutral" variant="ghost" aria-label="Remove schedule" @click="removeSchedule(i)" />
        </div>
        <p v-if="cronIsValid(s.cron)" class="-mt-1 text-xs text-primary">{{ describeCron(s.cron) }} <span class="text-muted">({{ s.timeZone || 'UTC' }})</span></p>
        <div class="grid gap-2 sm:grid-cols-2">
          <USelectMenu v-model="s.timeZone" :items="zones" size="sm" icon="i-lucide-globe" class="w-full" @update:model-value="saveSchedules" />
          <UInput v-model="s.branch" size="sm" icon="i-lucide-git-branch" class="w-full font-mono" :placeholder="defaultBranch" @update:model-value="saveSchedules" />
        </div>
        <KeyValueEditor v-model="s.vars" add-label="Add var" @update:model-value="saveSchedules" />
      </div>
    </div>
  </div>
</template>
