/** Browser link to a file in its repository, for Azure DevOps and GitHub https remotes; null otherwise. */
export function repoFileUrl(repoUrl: string, path: string, branch: string): string | null {
  let url: URL
  try { url = new URL(repoUrl.trim()) } catch { return null }
  if (url.protocol !== 'https:') return null
  url.username = ''
  url.password = ''
  const clean = path.replace(/^\/+/, '')
  const host = url.hostname.toLowerCase()
  if ((host === 'dev.azure.com' || host.endsWith('.visualstudio.com')) && url.pathname.includes('/_git/')) {
    const base = `${url.origin}${url.pathname.replace(/\/+$/, '')}`
    return `${base}?path=/${encodeURI(clean)}&version=GB${encodeURIComponent(branch)}`
  }
  if (host === 'github.com') {
    const base = `${url.origin}${url.pathname.replace(/\/+$/, '').replace(/\.git$/, '')}`
    return `${base}/blob/${encodeURI(branch)}/${encodeURI(clean)}`
  }
  return null
}
