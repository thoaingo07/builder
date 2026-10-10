<script setup lang="ts">
import { computed } from 'vue'
import type { TaskModel } from '@/lib/taskfile'
import KeyValueList from './KeyValueList.vue'
import ContainerDeployDetails from '@/components/ContainerDeployDetails.vue'

/** Read-only details of one task in a runner file. */
const props = defineProps<{ task: TaskModel; isEntry: boolean; reachable: boolean }>()
const emit = defineEmits<{ select: [task: string] }>()

const deploy = computed(() => {
  const d = props.task.deploy
  if (!d) return []
  return (['compose', 'project', 'manifests', 'namespace', 'url'] as const).filter(k => d[k]).map(k => ({ key: k, value: d[k] }))
})
const kindMeta = {
  cmd: { label: 'Command', icon: 'i-lucide-terminal' },
  task: { label: 'Run task', icon: 'i-lucide-corner-down-right' },
  defer: { label: 'Cleanup', icon: 'i-lucide-undo-2' },
} as const
</script>

<template>
  <div class="space-y-5 text-sm">
    <div>
      <div class="flex flex-wrap items-center gap-2">
        <h3 class="font-mono text-base font-semibold text-highlighted">{{ task.name }}</h3>
        <UBadge v-if="isEntry" icon="i-lucide-flag" label="Entry task" color="primary" variant="subtle" size="sm" />
        <UBadge v-if="!reachable" label="Not run by this entry" color="neutral" variant="outline" size="sm" />
      </div>
      <p v-if="task.desc" class="mt-1 text-muted">{{ task.desc }}</p>
    </div>

    <section v-if="task.deps.length" class="space-y-1">
      <h4 class="text-xs font-medium text-muted">Depends on <span class="font-normal">(run first, in parallel)</span></h4>
      <div class="flex flex-wrap gap-1">
        <UButton v-for="d in task.deps" :key="d" :label="d" size="xs" color="neutral" variant="soft" class="font-mono" @click="emit('select', d)" />
      </div>
      <p v-if="task.hasComplexDeps" class="text-xs text-muted">Some dependencies pass vars — see the YAML tab.</p>
    </section>

    <section class="space-y-1.5">
      <h4 class="text-xs font-medium text-muted">Steps <span class="font-normal">(cmds, in order)</span></h4>
      <p v-if="!task.steps.length" class="text-xs text-muted">No steps{{ task.approval ? ' — an approval gate' : '' }}.</p>
      <ol class="space-y-1.5">
        <li v-for="(s, i) in task.steps" :key="i" class="rounded-md border border-default p-2">
          <div class="flex items-center gap-1.5 text-xs">
            <span class="w-4 text-center text-muted tabular-nums">{{ i + 1 }}</span>
            <UIcon :name="s.complex ? 'i-lucide-file-code' : kindMeta[s.kind].icon" class="text-muted" />
            <span class="font-medium">{{ s.complex ? 'Advanced' : kindMeta[s.kind].label }}</span>
            <span class="flex-1" />
            <UBadge v-if="s.silent" label="silent" size="sm" color="neutral" variant="outline" />
            <UBadge v-if="s.ignoreError" label="ignore_error" size="sm" color="warning" variant="outline" />
          </div>
          <pre v-if="s.complex" class="mt-1 overflow-x-auto rounded bg-elevated p-1.5 font-mono text-[11px] whitespace-pre-wrap">{{ s.raw }}</pre>
          <template v-else-if="s.kind === 'task'">
            <UButton :label="s.task" size="xs" color="primary" variant="link" class="mt-0.5 px-0 font-mono" icon="i-lucide-corner-down-right" @click="emit('select', s.task)" />
            <KeyValueList v-if="s.vars.length" :entries="s.vars" class="mt-1 pl-5" />
          </template>
          <pre v-else class="mt-1 overflow-x-auto rounded bg-elevated p-1.5 font-mono text-[11px] whitespace-pre-wrap">{{ s.cmd }}</pre>
        </li>
      </ol>
    </section>

    <section v-if="task.vars.length" class="space-y-1">
      <h4 class="text-xs font-medium text-muted">Variables <code class="font-normal">vars</code></h4>
      <KeyValueList :entries="task.vars" />
    </section>
    <section v-if="task.env.length" class="space-y-1">
      <h4 class="text-xs font-medium text-muted">Environment <code class="font-normal">env</code></h4>
      <KeyValueList :entries="task.env" />
    </section>
    <section v-if="task.requires.length" class="space-y-1">
      <h4 class="text-xs font-medium text-muted">Run inputs <code class="font-normal">requires.vars</code></h4>
      <ul class="space-y-1">
        <li v-for="r in task.requires" :key="r.name" class="flex flex-wrap items-center gap-1 text-xs">
          <UIcon name="i-lucide-text-cursor-input" class="text-info" />
          <span class="font-mono font-medium">{{ r.name }}</span>
          <span v-if="!r.enum.length" class="text-muted">any value</span>
          <UBadge v-for="v in r.enum" :key="v" :label="v" size="sm" color="neutral" variant="soft" class="font-mono" />
        </li>
      </ul>
    </section>

    <section
      v-if="task.labels.length || task.artifacts.length || task.secrets.length || task.registries.length || task.azureArtifacts"
      class="space-y-2"
    >
      <h4 class="text-xs font-medium text-muted">Builder settings</h4>
      <dl class="grid grid-cols-[auto_1fr] items-start gap-x-3 gap-y-1.5 text-xs">
        <template v-if="task.labels.length">
          <dt class="pt-0.5 text-muted">Agent labels</dt>
          <dd class="flex flex-wrap gap-1"><UBadge v-for="l in task.labels" :key="l" :label="l" size="sm" color="neutral" variant="outline" class="font-mono" /></dd>
        </template>
        <template v-if="task.artifacts.length">
          <dt class="pt-0.5 text-muted">Artifacts</dt>
          <dd class="flex flex-wrap gap-1"><UBadge v-for="a in task.artifacts" :key="a" :label="a" icon="i-lucide-package" size="sm" color="neutral" variant="soft" class="font-mono" /></dd>
        </template>
        <template v-if="task.secrets.length">
          <dt class="pt-0.5 text-muted">Secrets</dt>
          <dd class="flex flex-wrap gap-1"><UBadge v-for="x in task.secrets" :key="x" :label="x" icon="i-lucide-key-round" size="sm" color="warning" variant="subtle" class="font-mono" /></dd>
        </template>
        <template v-if="task.registries.length">
          <dt class="pt-0.5 text-muted">Registries</dt>
          <dd class="space-y-0.5">
            <div v-for="r in task.registries" :key="r.registry" class="font-mono">
              {{ r.registry }} <span v-if="r.connection" class="text-muted">via {{ r.connection }}</span>
            </div>
          </dd>
        </template>
        <template v-if="task.azureArtifacts">
          <dt class="text-muted">Azure Artifacts</dt>
          <dd>token in <code>$VSS_NUGET_ACCESSTOKEN</code> / <code>$AZURE_DEVOPS_TOKEN</code></dd>
        </template>
      </dl>
    </section>

    <UAlert
      v-if="task.approval" color="warning" variant="subtle" icon="i-lucide-lock" :title="task.approval.message || 'Approval required'"
      :description="task.approval.approvers.length ? `Approvers: ${task.approval.approvers.join(', ')}` : 'Any member can approve.'"
    />

    <UCard v-if="task.deploy" :ui="{ body: 'p-3 sm:p-3' }">
      <div class="mb-2 flex items-center gap-2 text-sm font-medium">
        <UIcon name="i-lucide-rocket" class="text-primary" /> Deploys to <span class="font-semibold">{{ task.deploy.environment || '—' }}</span>
      </div>
      <KeyValueList :entries="deploy" />
      <ContainerDeployDetails v-if="task.deploy.container" :c="task.deploy.container" class="mt-2 border-t border-default pt-2" />
    </UCard>
  </div>
</template>
