import { createRouter, createWebHistory } from 'vue-router'
import { setUnauthorizedHandler } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { useLiveStore } from '@/stores/live'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
    {
      path: '/',
      component: () => import('@/components/AppShell.vue'),
      children: [
        { path: '', name: 'dashboard', component: () => import('@/views/DashboardView.vue') },
        { path: 'builds', name: 'builds', component: () => import('@/views/BuildsView.vue') },
        { path: 'builds/:id', name: 'build', component: () => import('@/views/BuildDetailView.vue'), props: true },
        { path: 'pipelines', name: 'pipelines', component: () => import('@/views/PipelinesView.vue') },
        { path: 'pipelines/:id/editor', name: 'pipeline-editor', component: () => import('@/views/PipelineEditorView.vue'), props: true },
        { path: 'agents', name: 'agents', component: () => import('@/views/AgentsView.vue') },
        { path: 'environments', name: 'environments', component: () => import('@/views/EnvironmentsView.vue') },
        { path: 'deployments', name: 'deployments', component: () => import('@/views/DeploymentsView.vue') },
        { path: 'connections', name: 'connections', component: () => import('@/views/ConnectionsView.vue') },
        { path: 'cleanup', name: 'cleanup', component: () => import('@/views/CleanupView.vue') },
      ],
    },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

router.beforeEach(async to => {
  const auth = useAuthStore()
  if (!auth.checked) await auth.load()
  if (to.meta.public) return auth.user && to.name === 'login' ? { path: '/' } : true
  if (!auth.user) return { name: 'login', query: { redirect: to.fullPath } }
  void useLiveStore().start()
  return true
})

setUnauthorizedHandler(() => {
  const auth = useAuthStore()
  if (!auth.user) return
  auth.clear()
  void useLiveStore().stop()
  const current = router.currentRoute.value
  if (!current.meta.public) void router.push({ name: 'login', query: { redirect: current.fullPath } })
})
