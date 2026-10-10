import { createRouter, createWebHistory } from 'vue-router'
import { setOrgInvalidHandler, setUnauthorizedHandler } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { useLiveStore } from '@/stores/live'
import { useOrgStore } from '@/stores/org'
import { useProjectStore } from '@/stores/project'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
    // the runner guide is public: coding agents and colleagues read it without an account
    { path: '/guide', name: 'guide', component: () => import('@/views/GuideView.vue'), meta: { public: true } },
    { path: '/welcome', name: 'onboarding', component: () => import('@/views/OnboardingView.vue') },
    {
      path: '/',
      component: () => import('@/components/AppShell.vue'),
      children: [
        { path: '', name: 'dashboard', component: () => import('@/views/DashboardView.vue') },
        { path: 'builds', name: 'builds', component: () => import('@/views/BuildsView.vue') },
        { path: 'builds/:id', name: 'build', component: () => import('@/views/BuildDetailView.vue'), props: true },
        // "Runners" in the UI; the routes keep the API's pipeline naming
        { path: 'pipelines', name: 'runners', component: () => import('@/views/PipelinesView.vue') },
        { path: 'pipelines/:id/editor', name: 'runner-editor', component: () => import('@/views/PipelineEditorView.vue'), props: true },
        { path: 'repositories', name: 'repositories', component: () => import('@/views/RepositoriesView.vue') },
        { path: 'repositories/:id', name: 'repository', component: () => import('@/views/RepositoryDetailView.vue'), props: true },
        { path: 'agents', name: 'agents', component: () => import('@/views/AgentsView.vue') },
        { path: 'environments', name: 'environments', component: () => import('@/views/EnvironmentsView.vue') },
        { path: 'deployments', name: 'deployments', component: () => import('@/views/DeploymentsView.vue') },
        { path: 'connections', name: 'connections', component: () => import('@/views/ConnectionsView.vue') },
        { path: 'secrets', name: 'secrets', component: () => import('@/views/SecretsView.vue') },
        { path: 'projects', name: 'projects', component: () => import('@/views/ProjectsView.vue') },
        { path: 'settings', name: 'settings', component: () => import('@/views/SettingsView.vue') },
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

  const org = useOrgStore()
  if (!org.loaded) {
    try { await org.load() } catch { /* 401 is handled by the client; anything else: show onboarding/empty states */ }
  }
  if (!org.orgs.length) return to.name === 'onboarding' ? true : { name: 'onboarding' }
  if (to.name === 'onboarding') return { path: '/' }
  await useProjectStore().ensureLoaded()

  const live = useLiveStore()
  void live.joinOrg(org.currentId)
  void live.start()
  return true
})

setUnauthorizedHandler(() => {
  const auth = useAuthStore()
  if (!auth.user) return
  auth.clear()
  useOrgStore().reset()
  void useLiveStore().stop()
  const current = router.currentRoute.value
  if (!current.meta.public) void router.push({ name: 'login', query: { redirect: current.fullPath } })
})

setOrgInvalidHandler(() => {
  const org = useOrgStore()
  org.invalidate()
  void useLiveStore().joinOrg(org.currentId)
  void router.push(org.orgs.length ? { path: '/' } : { name: 'onboarding' })
})
