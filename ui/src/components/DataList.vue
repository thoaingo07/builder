<script setup lang="ts" generic="T extends { id: string }">
import { computed, useSlots } from 'vue'
import type { TableColumn, TableRow } from '@nuxt/ui'
import { useIsMobile } from '@/composables/useIsMobile'

/**
 * A table on md+ and stacked cards on phones. Table cell slots are passed through to UTable;
 * the `card` slot renders one item on small screens. `highlightId` flashes a row/card (useHighlight).
 */
const props = withDefaults(defineProps<{
  data: T[]
  columns: TableColumn<T>[]
  loading?: boolean
  empty?: string
  highlightId?: string | null
  clickable?: boolean
}>(), { loading: false, empty: 'Nothing here yet', highlightId: null, clickable: false })
const emit = defineEmits<{ select: [item: T] }>()
const slots = useSlots()
const mobile = useIsMobile()

const tableSlots = computed(() => Object.keys(slots).filter(n => n !== 'card'))
const meta = computed(() => ({
  class: {
    tr: (row: TableRow<T>) => [`hl-id-${row.original.id}`, row.original.id === props.highlightId ? 'hl-flash' : '', props.clickable ? 'cursor-pointer' : ''].join(' '),
  },
}))
</script>

<template>
  <div v-if="mobile" class="divide-y divide-default">
    <template v-if="loading && !data.length">
      <div v-for="i in 3" :key="i" class="p-3"><USkeleton class="h-14 w-full" /></div>
    </template>
    <p v-else-if="!data.length" class="p-6 text-center text-sm text-muted">{{ empty }}</p>
    <div
      v-for="item in data" v-else :key="item.id" :data-hl="item.id"
      class="min-w-0 p-3" :class="[item.id === highlightId ? 'hl-flash' : '', clickable ? 'cursor-pointer active:bg-elevated/60' : '']"
      @click="clickable && emit('select', item)"
    >
      <slot name="card" :item="item" />
    </div>
  </div>
  <UTable
    v-else :data="data" :columns="columns" :loading="loading" :empty="empty" :meta="meta" class="w-full"
    @select="(_e: Event, row: TableRow<T>) => clickable && emit('select', row.original)"
  >
    <template v-for="name in tableSlots" :key="name" #[name]="scope">
      <slot :name="name" v-bind="scope" />
    </template>
  </UTable>
</template>
