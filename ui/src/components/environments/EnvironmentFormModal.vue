<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { FormError } from '@nuxt/ui'
import { api } from '@/api/client'
import type { EnvironmentDto, EnvironmentInput, EnvironmentType } from '@/api/types'
import { useNotify } from '@/composables/useNotify'

const props = defineProps<{ environment: EnvironmentDto | null }>()
const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ saved: [EnvironmentDto] }>()
const notify = useNotify()

const blank = () => ({
  name: '', type: 'SshDocker' as EnvironmentType, requiresApproval: false, approvers: [] as string[], agentLabels: [] as string[],
  host: '', port: 22, username: '', privateKey: '',
  k8sMode: 'kubeconfig' as 'kubeconfig' | 'aks', kubeconfig: '',
  aksTenantId: '', aksClientId: '', aksClientSecret: '', aksSubscriptionId: '', aksResourceGroup: '', aksClusterName: '', aksAdmin: false,
})
const s = reactive(blank())
const saving = ref(false)
const env = computed(() => props.environment)

const typeItems = [
  { label: 'VPS · SSH + Docker Compose', value: 'SshDocker', icon: 'i-lucide-server' },
  { label: 'Kubernetes / AKS', value: 'Kubernetes', icon: 'i-lucide-ship-wheel' },
]
const k8sModes = [
  { label: 'Kubeconfig', value: 'kubeconfig' },
  { label: 'Azure AKS (service principal)', value: 'aks' },
]

watch(open, o => {
  if (!o) return
  const e = props.environment
  Object.assign(s, blank())
  if (!e) return
  Object.assign(s, {
    name: e.name, type: e.type, requiresApproval: e.requiresApproval, approvers: [...e.approvers], agentLabels: [...e.agentLabels],
    host: e.host ?? '', port: e.port || 22, username: e.username ?? '',
    k8sMode: e.aksClusterName ? 'aks' : 'kubeconfig',
    aksTenantId: e.aksTenantId ?? '', aksClientId: e.aksClientId ?? '', aksSubscriptionId: e.aksSubscriptionId ?? '',
    aksResourceGroup: e.aksResourceGroup ?? '', aksClusterName: e.aksClusterName ?? '', aksAdmin: e.aksAdmin,
  })
}, { immediate: true })

const secretHint = (stored: boolean | undefined) => (stored ? 'Stored ✓ — leave empty to keep it' : undefined)

function validate(v: typeof s): FormError[] {
  const errors: FormError[] = []
  if (!v.name.trim()) errors.push({ name: 'name', message: 'Required' })
  if (v.type === 'SshDocker') {
    if (!v.host.trim()) errors.push({ name: 'host', message: 'Required' })
    if (!v.username.trim()) errors.push({ name: 'username', message: 'Required' })
    if (!v.privateKey.trim() && !env.value?.hasPrivateKey) errors.push({ name: 'privateKey', message: 'Required' })
  } else if (v.k8sMode === 'kubeconfig') {
    if (!v.kubeconfig.trim() && !env.value?.hasKubeconfig) errors.push({ name: 'kubeconfig', message: 'Required' })
  } else {
    for (const k of ['aksTenantId', 'aksClientId', 'aksSubscriptionId', 'aksResourceGroup', 'aksClusterName'] as const)
      if (!v[k].trim()) errors.push({ name: k, message: 'Required' })
    if (!v.aksClientSecret.trim() && !env.value?.hasAksClientSecret) errors.push({ name: 'aksClientSecret', message: 'Required' })
  }
  return errors
}

const orNull = (v: string) => (v.trim() ? v.trim() : null)

async function submit() {
  saving.value = true
  const aks = s.type === 'Kubernetes' && s.k8sMode === 'aks'
  const input: EnvironmentInput = {
    name: s.name.trim(), type: s.type, requiresApproval: s.requiresApproval, approvers: s.approvers, agentLabels: s.agentLabels,
    host: s.type === 'SshDocker' ? orNull(s.host) : null, port: Number(s.port) || 22,
    username: s.type === 'SshDocker' ? orNull(s.username) : null,
    privateKey: s.type === 'SshDocker' ? (s.privateKey.trim() ? s.privateKey : null) : null,
    kubeconfig: s.type === 'Kubernetes' && !aks ? (s.kubeconfig.trim() ? s.kubeconfig : null) : null,
    aksTenantId: aks ? orNull(s.aksTenantId) : null, aksClientId: aks ? orNull(s.aksClientId) : null,
    aksClientSecret: aks ? orNull(s.aksClientSecret) : null, aksSubscriptionId: aks ? orNull(s.aksSubscriptionId) : null,
    aksResourceGroup: aks ? orNull(s.aksResourceGroup) : null, aksClusterName: aks ? orNull(s.aksClusterName) : null,
    aksAdmin: aks && s.aksAdmin,
  }
  try {
    const saved = env.value ? await api.environments.update(env.value.id, input) : await api.environments.create(input)
    notify.success(env.value ? 'Environment updated' : 'Environment created')
    emit('saved', saved)
    open.value = false
  } catch (e) {
    notify.error(e, 'Could not save environment')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" :title="env ? `Edit ${env.name}` : 'New environment'" description="A deploy target used by x-deploy tasks." :ui="{ content: 'sm:max-w-2xl' }">
    <template #body>
      <UForm id="env-form" :state="s" :validate="validate" class="space-y-5" @submit="submit">
        <div class="grid gap-4 sm:grid-cols-2">
          <UFormField label="Name" name="name" required help="Referenced as x-deploy.environment">
            <UInput v-model="s.name" class="w-full font-mono" placeholder="staging-vps" />
          </UFormField>
          <UFormField label="Type" name="type">
            <USelect v-model="s.type" :items="typeItems" class="w-full" />
          </UFormField>
        </div>

        <template v-if="s.type === 'SshDocker'">
          <div class="grid gap-4 sm:grid-cols-[1fr_7rem_1fr]">
            <UFormField label="Host" name="host" required>
              <UInput v-model="s.host" class="w-full font-mono" placeholder="203.0.113.10" />
            </UFormField>
            <UFormField label="Port" name="port">
              <UInputNumber v-model="s.port" :min="1" :max="65535" class="w-full" />
            </UFormField>
            <UFormField label="User" name="username" required>
              <UInput v-model="s.username" class="w-full font-mono" placeholder="deploy" />
            </UFormField>
          </div>
          <UFormField label="Private key" name="privateKey" :required="!env?.hasPrivateKey" :help="secretHint(env?.hasPrivateKey) ?? 'OpenSSH private key; the user needs docker access on the host.'">
            <UTextarea v-model="s.privateKey" :rows="4" class="w-full font-mono text-xs" placeholder="-----BEGIN OPENSSH PRIVATE KEY-----" autocomplete="off" spellcheck="false" />
          </UFormField>
        </template>

        <template v-else>
          <URadioGroup v-model="s.k8sMode" :items="k8sModes" orientation="horizontal" />
          <UFormField v-if="s.k8sMode === 'kubeconfig'" label="Kubeconfig" name="kubeconfig" :help="secretHint(env?.hasKubeconfig)">
            <UTextarea v-model="s.kubeconfig" :rows="6" class="w-full font-mono text-xs" placeholder="apiVersion: v1&#10;kind: Config…" spellcheck="false" />
          </UFormField>
          <div v-else class="grid gap-4 sm:grid-cols-2">
            <UFormField label="Tenant ID" name="aksTenantId" required><UInput v-model="s.aksTenantId" class="w-full font-mono" /></UFormField>
            <UFormField label="Subscription ID" name="aksSubscriptionId" required><UInput v-model="s.aksSubscriptionId" class="w-full font-mono" /></UFormField>
            <UFormField label="Client ID" name="aksClientId" required><UInput v-model="s.aksClientId" class="w-full font-mono" /></UFormField>
            <UFormField label="Client secret" name="aksClientSecret" :help="secretHint(env?.hasAksClientSecret)">
              <UInput v-model="s.aksClientSecret" type="password" class="w-full" autocomplete="new-password" />
            </UFormField>
            <UFormField label="Resource group" name="aksResourceGroup" required><UInput v-model="s.aksResourceGroup" class="w-full font-mono" /></UFormField>
            <UFormField label="Cluster name" name="aksClusterName" required><UInput v-model="s.aksClusterName" class="w-full font-mono" /></UFormField>
            <UCheckbox v-model="s.aksAdmin" label="Use admin credentials" description="az aks get-credentials --admin (local accounts must be enabled)." class="sm:col-span-2" />
          </div>
        </template>

        <USeparator />

        <div class="grid gap-4 sm:grid-cols-2">
          <USwitch v-model="s.requiresApproval" label="Require approval for every deploy" class="sm:col-span-2" />
          <UFormField v-if="s.requiresApproval" label="Approvers" help="Empty = any signed-in user" class="sm:col-span-2">
            <UInputTags v-model="s.approvers" class="w-full" />
          </UFormField>
          <UFormField label="Agent labels" help="Deploy jobs only run on agents with these labels." class="sm:col-span-2">
            <UInputTags v-model="s.agentLabels" class="w-full" placeholder="deploy, ssh…" />
          </UFormField>
        </div>
      </UForm>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton color="neutral" variant="outline" label="Cancel" @click="open = false" />
        <UButton type="submit" form="env-form" :loading="saving" :label="env ? 'Save' : 'Create'" />
      </div>
    </template>
  </UModal>
</template>
