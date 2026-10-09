import ConfirmModal from '@/components/ConfirmModal.vue'

export interface ConfirmOptions {
  title: string
  message: string
  confirmLabel?: string
  danger?: boolean
}

/** Promise-based confirm dialog: `if (await confirm({...})) ...`. Must be called from setup. */
export function useConfirm() {
  const overlay = useOverlay()
  const modal = overlay.create(ConfirmModal)
  return async (opts: ConfirmOptions): Promise<boolean> => {
    const result = await modal.open(opts).result
    return result === true
  }
}
