import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api/client'
import type { BuildSummaryDto, DeploymentDto } from '@/api/types'
import { useConfirm } from './useConfirm'
import { useNotify } from './useNotify'

/** Cancel / re-run / delete / destroy actions with confirmation + toasts, shared by several views. */
export function useBuildActions() {
  const confirm = useConfirm()
  const notify = useNotify()
  const router = useRouter()
  const busy = ref<string | null>(null)

  async function cancel(b: Pick<BuildSummaryDto, 'id' | 'number' | 'pipelineName'>) {
    if (!await confirm({ title: 'Cancel build', message: `Cancel ${b.pipelineName} #${b.number}? Running jobs are stopped on their agents.`, confirmLabel: 'Cancel build', danger: true })) return null
    busy.value = `cancel:${b.id}`
    try {
      const r = await api.builds.cancel(b.id)
      notify.info(`Canceling ${b.pipelineName} #${b.number}`)
      return r
    } catch (e) { notify.error(e, 'Cancel failed'); return null } finally { busy.value = null }
  }

  async function rerun(b: Pick<BuildSummaryDto, 'id' | 'number' | 'pipelineName'>, navigate = true) {
    busy.value = `rerun:${b.id}`
    try {
      const r = await api.builds.rerun(b.id)
      notify.success(`Queued ${r.pipelineName} #${r.number}`)
      if (navigate) await router.push(`/builds/${r.id}`)
      return r
    } catch (e) { notify.error(e, 'Re-run failed'); return null } finally { busy.value = null }
  }

  async function remove(b: Pick<BuildSummaryDto, 'id' | 'number' | 'pipelineName'>) {
    if (!await confirm({ title: 'Delete build', message: `Delete ${b.pipelineName} #${b.number} with its logs and artifacts? This cannot be undone.`, confirmLabel: 'Delete', danger: true })) return false
    busy.value = `delete:${b.id}`
    try {
      await api.builds.remove(b.id)
      notify.success(`Deleted ${b.pipelineName} #${b.number}`)
      return true
    } catch (e) { notify.error(e, 'Delete failed'); return false } finally { busy.value = null }
  }

  async function destroyDeployment(d: DeploymentDto) {
    if (!await confirm({
      title: 'Destroy deployment',
      message: `Tear down "${d.name}" on ${d.environmentName}?\n${d.url ? `${d.url} will stop responding.` : ''}`,
      confirmLabel: 'Destroy', danger: true,
    })) return null
    busy.value = `destroy:${d.id}`
    try {
      const r = await api.deployments.destroy(d.id)
      notify.info(`Destroying ${d.name}`, 'Teardown runs on an agent.')
      return r
    } catch (e) { notify.error(e, 'Destroy failed'); return null } finally { busy.value = null }
  }

  return { busy, cancel, rerun, remove, destroyDeployment }
}
