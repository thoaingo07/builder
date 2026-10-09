<script setup lang="ts">
import type { ToasterProps } from '@nuxt/ui'

const toaster: ToasterProps = { position: 'bottom-right', expand: false }
</script>

<template>
  <UApp :toaster="toaster">
    <div class="isolate">
      <RouterView />
    </div>
  </UApp>
</template>
