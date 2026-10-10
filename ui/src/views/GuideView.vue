<script setup lang="ts">
// Renders docs/runner-guide.md (served at /runner-guide.md). Public: no sign-in needed.
import { marked } from 'marked'
import { computed, onMounted, ref } from 'vue'
import guide from '../../../docs/runner-guide.md?raw'

const html = computed(() => marked.parse(guide, { async: false, gfm: true }) as string)
const rawUrl = ref('')
const copied = ref(false)

onMounted(() => { rawUrl.value = `${window.location.origin}/runner-guide.md` })

async function copyPrompt() {
  const prompt = `Read ${rawUrl.value} and follow it to create a Builder runner file (.builder/runners/<name>.yml) for this repository. `
    + 'Open a pull request with it, and list the secrets, connections and environments a person must create in Builder.'
  try {
    await navigator.clipboard.writeText(prompt)
    copied.value = true
    setTimeout(() => { copied.value = false }, 2000)
  } catch { /* clipboard blocked: the URL is shown on the page */ }
}
</script>

<template>
  <div class="min-h-dvh bg-default">
    <header class="sticky top-0 z-10 border-b border-default bg-default/90 backdrop-blur">
      <div class="mx-auto flex max-w-4xl flex-wrap items-center gap-2 px-4 py-3">
        <RouterLink to="/" class="flex items-center gap-2 font-semibold">
          <UIcon name="i-lucide-hammer" class="size-5 text-primary" /> Builder
        </RouterLink>
        <span class="text-muted">/ Runner guide</span>
        <div class="ms-auto flex flex-wrap gap-2">
          <UButton size="sm" variant="soft" icon="i-lucide-file-text" :to="rawUrl || '/runner-guide.md'" target="_blank" label="Markdown" />
          <UButton size="sm" :icon="copied ? 'i-lucide-check' : 'i-lucide-bot'" :label="copied ? 'Copied' : 'Copy prompt for an agent'" @click="copyPrompt" />
        </div>
      </div>
    </header>
    <main class="mx-auto max-w-4xl px-4 py-6">
      <UAlert
        class="mb-6" color="primary" variant="subtle" icon="i-lucide-bot"
        title="For coding agents"
        :description="`Point the agent at ${rawUrl || '/runner-guide.md'} (plain Markdown, no sign-in).`"
      />
      <!-- eslint-disable-next-line vue/no-v-html -- our own document, rendered at build time from the repository -->
      <article class="guide" v-html="html" />
    </main>
  </div>
</template>

<style scoped>
.guide :deep(h1) { font-size: 1.75rem; font-weight: 700; margin: 0 0 1rem; }
.guide :deep(h2) { font-size: 1.35rem; font-weight: 700; margin: 2.25rem 0 0.75rem; padding-top: 1rem; border-top: 1px solid var(--ui-border); }
.guide :deep(h3) { font-size: 1.1rem; font-weight: 600; margin: 1.75rem 0 0.5rem; }
.guide :deep(p), .guide :deep(ul), .guide :deep(ol) { margin: 0.6rem 0; line-height: 1.65; }
.guide :deep(ul) { list-style: disc; padding-left: 1.4rem; }
.guide :deep(ol) { list-style: decimal; padding-left: 1.4rem; }
.guide :deep(li) { margin: 0.2rem 0; }
.guide :deep(a) { color: var(--ui-primary); text-decoration: underline; }
.guide :deep(code) { font-family: ui-monospace, monospace; font-size: 0.85em; background: var(--ui-bg-elevated); padding: 0.1rem 0.3rem; border-radius: 0.25rem; overflow-wrap: anywhere; }
.guide :deep(pre) { background: var(--ui-bg-elevated); border: 1px solid var(--ui-border); border-radius: 0.5rem; padding: 0.9rem 1rem; overflow-x: auto; margin: 0.9rem 0; }
.guide :deep(pre code) { background: none; padding: 0; font-size: 0.8rem; overflow-wrap: normal; }
.guide :deep(table) { display: block; overflow-x: auto; border-collapse: collapse; margin: 0.9rem 0; font-size: 0.9rem; }
.guide :deep(th), .guide :deep(td) { border: 1px solid var(--ui-border); padding: 0.4rem 0.6rem; text-align: left; vertical-align: top; }
.guide :deep(th) { background: var(--ui-bg-elevated); }
.guide :deep(strong) { font-weight: 600; }
.guide :deep(h2), .guide :deep(h3) { scroll-margin-top: 6rem; }
</style>
