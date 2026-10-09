<script setup lang="ts">
import { computed } from 'vue'
import { useCopy } from '@/composables/useClipboard'

/** Shows an organization's agent token (once) plus how to start a daemon with it. Without a token it only explains. */
const props = defineProps<{ token: string | null; orgName: string; title?: string }>()
const open = defineModel<boolean>('open', { required: true })
const copy = useCopy()

const tokenText = computed(() => props.token ?? '<agent-token>')
const docker = computed(() => [
  'docker run -d --name builder-agent --restart unless-stopped \\',
  '  -e Agent__ServerUrl=https://YOUR-API \\',
  `  -e Agent__Token=${tokenText.value} \\`,
  '  localhost/builder-agent',
].join('\n'))
const dotnet = computed(() => `Agent__ServerUrl=https://YOUR-API Agent__Token=${tokenText.value} dotnet Builder.Agent.dll`)
</script>

<template>
  <UModal v-model:open="open" :title="title ?? 'Add an agent'" :description="`Agents run ${orgName}'s jobs. They connect out to the API, so no inbound ports are needed.`" :ui="{ content: 'sm:max-w-2xl' }">
    <template #body>
      <div class="space-y-5">
        <template v-if="token">
          <UAlert color="warning" variant="subtle" icon="i-lucide-triangle-alert" title="Copy the token now — it is shown only once."
            description="Anyone with it can register agents for this organization. Regenerating it disconnects agents that use the old one." />
          <UFormField label="Agent token">
            <div class="flex gap-2">
              <UInput :model-value="token" readonly class="flex-1" :ui="{ base: 'font-mono text-xs' }" />
              <UButton icon="i-lucide-copy" label="Copy" @click="copy(token!, 'Token copied')" />
            </div>
          </UFormField>
        </template>
        <UAlert v-else color="neutral" variant="subtle" icon="i-lucide-key-round" title="You need the organization's agent token"
          description="It is shown once when the organization is created or the token is regenerated (Admins: Settings → Agent token)." />

        <div class="space-y-2">
          <div class="flex items-center justify-between">
            <span class="text-sm font-medium">Container (Docker or Podman)</span>
            <UButton icon="i-lucide-copy" size="xs" color="neutral" variant="ghost" label="Copy" @click="copy(docker, 'Command copied')" />
          </div>
          <pre class="overflow-x-auto rounded-md bg-elevated p-3 font-mono text-xs">{{ docker }}</pre>
        </div>
        <div class="space-y-2">
          <div class="flex items-center justify-between">
            <span class="text-sm font-medium">.NET (published Builder.Agent)</span>
            <UButton icon="i-lucide-copy" size="xs" color="neutral" variant="ghost" label="Copy" @click="copy(dotnet, 'Command copied')" />
          </div>
          <pre class="overflow-x-auto rounded-md bg-elevated p-3 font-mono text-xs break-all whitespace-pre-wrap">{{ dotnet }}</pre>
        </div>
        <p class="text-xs text-muted">
          Replace <code>https://YOUR-API</code> with the address of the Builder API (not this web UI).
          Agents need <code>git</code> and <code>task</code>; add <code>docker</code>/<code>podman</code>, <code>kubectl</code> or <code>az</code> for deploy jobs.
        </p>
      </div>
    </template>
    <template #footer>
      <div class="flex w-full justify-end">
        <UButton :label="token ? 'I copied the token' : 'Close'" @click="open = false" />
      </div>
    </template>
  </UModal>
</template>
