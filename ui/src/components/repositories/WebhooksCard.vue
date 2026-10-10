<script setup lang="ts">
import { computed, ref } from 'vue'
import { api } from '@/api/client'
import type { ConnectionType, HookSetupDto, RepositoryDto } from '@/api/types'
import { useNotify } from '@/composables/useNotify'
import { useConfirm } from '@/composables/useConfirm'
import { useCopy } from '@/composables/useClipboard'

/** Push / pull-request webhooks from the git host into Builder (Admin). */
const props = defineProps<{ repo: RepositoryDto; connectionType: ConnectionType | null }>()
const notify = useNotify()
const confirm = useConfirm()
const copy = useCopy()

const installing = ref(false)
const generating = ref(false)
const installResult = ref<string | null>(null)
const installError = ref<string | null>(null)
const setup = ref<HookSetupDto | null>(null)
const manualOpen = ref(false)

const isAzure = computed(() => props.connectionType === 'AzureDevOps')
const origin = () => window.location.origin
const httpsWarning = computed(() => !window.location.origin.startsWith('https://'))

async function install() {
  if (!await confirm({
    title: 'Install service hooks',
    message: 'Builder rotates this repository\'s webhook secret and creates the Azure DevOps service hooks (push, pull request created, pull request updated). Hooks set up manually with the old secret stop working.',
    confirmLabel: 'Install',
  })) return
  installing.value = true
  installResult.value = null
  installError.value = null
  try {
    const r = await api.repositories.installHook(props.repo.id, origin())
    installResult.value = `${r.installed ?? 0} service hook${r.installed === 1 ? '' : 's'} installed`
    notify.success(installResult.value, r.url)
  } catch (e) {
    installError.value = e instanceof Error ? e.message : 'Install failed'
  } finally {
    installing.value = false
  }
}

async function manual() {
  if (!await confirm({
    title: 'Generate a webhook secret',
    message: 'A new secret is created and shown once. Webhooks using the previous secret (including installed service hooks) stop working until they are updated.',
    confirmLabel: 'Generate',
    danger: true,
  })) return
  generating.value = true
  try {
    setup.value = await api.repositories.hook(props.repo.id, origin())
    manualOpen.value = true
  } catch (e) {
    notify.error(e, 'Could not create the webhook secret')
  } finally {
    generating.value = false
  }
}
</script>

<template>
  <UCard>
    <template #header>
      <div class="flex items-center gap-2">
        <UIcon name="i-lucide-webhook" class="text-muted" />
        <h2 class="flex-1 font-semibold text-highlighted">Webhooks</h2>
      </div>
      <p class="text-sm text-muted">Start builds on push and pull requests, using each runner's <code>x-builder.triggers</code>.</p>
    </template>

    <div class="space-y-3">
      <UAlert
        v-if="httpsWarning" color="warning" variant="subtle" icon="i-lucide-triangle-alert" title="Builder isn't served over https here"
        description="Git hosts can only deliver webhooks to a public https address. Set them up from Builder's public URL."
      />
      <div class="flex flex-wrap gap-2">
        <UButton
          v-if="isAzure" icon="i-simple-icons-azuredevops" label="Install in Azure DevOps" :loading="installing" @click="install"
        />
        <UButton icon="i-lucide-settings-2" label="Set up manually" color="neutral" variant="outline" :loading="generating" @click="manual" />
      </div>
      <UAlert v-if="installResult" color="success" variant="subtle" icon="i-lucide-circle-check" :title="installResult" />
      <UAlert v-if="installError" color="error" variant="subtle" icon="i-lucide-circle-x" title="Could not install the service hooks" :description="installError" />
      <p v-if="isAzure" class="text-xs text-muted">
        Installing needs a PAT or service principal that may manage service hooks in the project (Project Administrator, or "Edit subscriptions"),
        and Builder must be reachable over https from Azure DevOps.
      </p>
    </div>

    <UModal v-model:open="manualOpen" title="Webhook for this repository" description="The secret is shown only once." :ui="{ content: 'sm:max-w-2xl' }">
      <template #body>
        <div v-if="setup" class="space-y-4">
          <UAlert color="warning" variant="subtle" icon="i-lucide-triangle-alert" title="Copy the secret now"
            description="Generating a new secret invalidates this one (and any service hooks that use it)." />
          <UFormField label="URL">
            <div class="flex gap-2">
              <UInput :model-value="setup.url" readonly class="flex-1" :ui="{ base: 'font-mono text-xs' }" />
              <UButton icon="i-lucide-copy" color="neutral" variant="outline" aria-label="Copy URL" @click="copy(setup.url, 'URL copied')" />
            </div>
          </UFormField>
          <div class="grid gap-4 sm:grid-cols-[12rem_1fr]">
            <UFormField label="HTTP header">
              <div class="flex gap-2">
                <UInput :model-value="setup.header" readonly class="flex-1" :ui="{ base: 'font-mono text-xs' }" />
                <UButton icon="i-lucide-copy" color="neutral" variant="outline" aria-label="Copy header name" @click="copy(setup.header, 'Header copied')" />
              </div>
            </UFormField>
            <UFormField label="Secret">
              <div class="flex gap-2">
                <UInput :model-value="setup.secret ?? ''" readonly class="flex-1" :ui="{ base: 'font-mono text-xs' }" />
                <UButton icon="i-lucide-copy" color="neutral" variant="outline" aria-label="Copy secret" :disabled="!setup.secret" @click="copy(setup.secret!, 'Secret copied')" />
              </div>
            </UFormField>
          </div>
          <div class="space-y-1.5 text-sm">
            <div class="font-medium">In Azure DevOps</div>
            <ol class="list-decimal space-y-1 pl-5 text-muted">
              <li><em>Project settings → Service hooks → +</em> → <strong>Web Hooks</strong>.</li>
              <li>Create one subscription per event: <strong>Code pushed</strong>, <strong>Pull request created</strong>, <strong>Pull request updated</strong> — each filtered to repository <strong>{{ repo.name }}</strong>.</li>
              <li>URL: the URL above. HTTP headers: <code>{{ setup.header }}:&lt;secret&gt;</code></li>
              <li>Resource details to send: <strong>All</strong>. Test, then Finish.</li>
            </ol>
            <UButton
              icon="i-lucide-copy" label="Copy header line" size="xs" color="neutral" variant="outline"
              :disabled="!setup.secret" @click="copy(`${setup.header}:${setup.secret}`, 'Header line copied')"
            />
          </div>
        </div>
      </template>
      <template #footer>
        <div class="flex w-full justify-end"><UButton label="Done" @click="manualOpen = false" /></div>
      </template>
    </UModal>
  </UCard>
</template>
