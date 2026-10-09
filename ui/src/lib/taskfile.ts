import { Document, isMap, isScalar, isSeq, parseDocument, YAMLMap, YAMLSeq, type Node, type Pair } from 'yaml'

/**
 * Read model + comment-preserving mutations for go-task Taskfiles with Builder's x- extensions.
 * All writes go through the `yaml` Document so formatting and comments elsewhere survive.
 */

export interface CmdModel { kind: 'cmd' | 'task' | 'other'; text: string }
export interface ApprovalModel { message: string; approvers: string[] }
export interface DeployModel { environment: string; compose: string; project: string; manifests: string; namespace: string; url: string }

export interface TaskModel {
  name: string
  desc: string
  deps: string[]
  /** deps written as `{ task, vars }` maps — editable only in YAML */
  hasComplexDeps: boolean
  cmds: CmdModel[]
  /** true when every cmd is a plain string, so the form can edit the list */
  simpleCmds: boolean
  labels: string[]
  artifacts: string[]
  /** x-secrets: organization secret names passed to the task as vars + env */
  secrets: string[]
  approval: ApprovalModel | null
  deploy: DeployModel | null
}

export interface TaskfileModel {
  version: string
  entry: string
  tasks: TaskModel[]
}

export function parse(content: string): Document {
  return parseDocument(content, { prettyErrors: true })
}

/** Serialises without padding flow lists (`[a, b]`) or folding long commands. */
export function toYaml(doc: Document): string {
  return doc.toString({ flowCollectionPadding: false, lineWidth: 0 })
}

export function errorsOf(doc: Document): string[] {
  return doc.errors.map(e => e.message)
}

const str = (v: unknown): string => (v === null || v === undefined ? '' : String(v))

function scalarValue(n: unknown): unknown {
  return isScalar(n) ? n.value : n
}

function stringList(n: unknown): string[] {
  if (isSeq(n)) return n.items.map(i => str(scalarValue(i))).filter(Boolean)
  if (isScalar(n) && n.value !== null && n.value !== '') return [str(n.value)]
  return []
}

function tasksMap(doc: Document): YAMLMap | null {
  const t = doc.get('tasks', true)
  return isMap(t) ? t : null
}

function keyOf(pair: Pair): string {
  return str(scalarValue(pair.key))
}

function readTask(name: string, node: unknown): TaskModel {
  const t: TaskModel = {
    name, desc: '', deps: [], hasComplexDeps: false, cmds: [], simpleCmds: true,
    labels: [], artifacts: [], secrets: [], approval: null, deploy: null,
  }
  if (isSeq(node)) {
    t.cmds = node.items.map(readCmd)
  } else if (isScalar(node)) {
    if (node.value !== null && node.value !== '') t.cmds = [{ kind: 'cmd', text: str(node.value) }]
  } else if (isMap(node)) {
    t.desc = str(node.get('desc'))
    const deps = node.get('deps', true)
    if (isSeq(deps)) {
      for (const d of deps.items) {
        if (isMap(d)) { t.hasComplexDeps = true; const n = str(d.get('task')); if (n) t.deps.push(n) }
        else { const n = str(scalarValue(d)); if (n) t.deps.push(n) }
      }
    }
    const cmds = node.get('cmds', true)
    if (isSeq(cmds)) t.cmds = cmds.items.map(readCmd)
    else if (node.has('cmd')) t.cmds = [{ kind: 'cmd', text: str(node.get('cmd')) }]

    const agent = node.get('x-agent', true)
    if (isMap(agent)) t.labels = stringList(agent.get('labels', true))
    t.artifacts = stringList(node.get('x-artifacts', true))
    t.secrets = stringList(node.get('x-secrets', true))

    const approval = node.get('x-approval', true)
    if (isMap(approval)) t.approval = { message: str(approval.get('message')), approvers: stringList(approval.get('approvers', true)) }
    else if (isScalar(approval) && approval.value) t.approval = { message: '', approvers: [] }

    const deploy = node.get('x-deploy', true)
    if (isMap(deploy)) {
      t.deploy = {
        environment: str(deploy.get('environment')), compose: str(deploy.get('compose')), project: str(deploy.get('project')),
        manifests: str(deploy.get('manifests')), namespace: str(deploy.get('namespace')), url: str(deploy.get('url')),
      }
    }
  }
  t.simpleCmds = t.cmds.every(c => c.kind === 'cmd') && !(isMap(node) && node.has('cmd'))
  return t
}

function readCmd(n: unknown): CmdModel {
  if (isScalar(n)) return { kind: 'cmd', text: str(n.value) }
  if (isMap(n)) {
    if (n.has('task')) return { kind: 'task', text: str(n.get('task')) }
    if (n.has('cmd') && n.items.length === 1) return { kind: 'cmd', text: str(n.get('cmd')) }
    return { kind: 'other', text: String(n).trim() }
  }
  return { kind: 'other', text: str(n) }
}

export function read(doc: Document): TaskfileModel {
  const tasks = tasksMap(doc)
  return {
    version: str(doc.get('version')),
    entry: str(doc.getIn(['x-builder', 'entry'])),
    tasks: tasks ? tasks.items.map(p => readTask(keyOf(p as Pair), (p as Pair).value)) : [],
  }
}

/** Returns the task's mapping node, converting go-task's shorthand forms (string / list of cmds) to a map. */
function ensureTaskMap(doc: Document, name: string): YAMLMap {
  let tasks = tasksMap(doc)
  if (!tasks) {
    tasks = doc.createNode({}) as YAMLMap
    doc.set('tasks', tasks)
  }
  const node = tasks.get(name, true)
  if (isMap(node)) return node
  const map = new YAMLMap()
  if (isSeq(node)) map.set('cmds', node)
  else if (isScalar(node) && node.value !== null && node.value !== '') map.set('cmds', doc.createNode([node.value]))
  tasks.set(name, map)
  return map
}

function flowList(doc: Document, items: string[]): Node {
  const seq = doc.createNode(items) as YAMLSeq
  seq.flow = true
  return seq
}

/** Removes `path` from map and then any parents that became empty (e.g. an empty `x-approval`). */
function deleteAndPrune(map: YAMLMap, path: string[]) {
  map.deleteIn(path)
  for (let i = path.length - 1; i > 0; i--) {
    const parent = map.getIn(path.slice(0, i), true)
    if (isMap(parent) && parent.items.length === 0) map.deleteIn(path.slice(0, i))
    else break
  }
}

export function setDesc(doc: Document, task: string, desc: string) {
  const m = ensureTaskMap(doc, task)
  if (desc.trim()) m.set('desc', desc)
  else m.delete('desc')
}

export function setCmds(doc: Document, task: string, cmds: string[]) {
  const m = ensureTaskMap(doc, task)
  const clean = cmds.filter(c => c.trim() !== '')
  m.delete('cmd')
  if (clean.length) m.set('cmds', doc.createNode(clean))
  else m.delete('cmds')
}

export function setDeps(doc: Document, task: string, deps: string[]) {
  const m = ensureTaskMap(doc, task)
  const existing = m.get('deps', true)
  // keep `{ task, vars }` items for deps that remain, so parameters aren't lost
  const keep = new Map<string, unknown>()
  if (isSeq(existing)) for (const d of existing.items) if (isMap(d)) keep.set(str(d.get('task')), d)
  const unique = [...new Set(deps.filter(Boolean))]
  if (!unique.length) { m.delete('deps'); return }
  const seq = new YAMLSeq()
  seq.flow = unique.every(d => !keep.has(d))
  for (const d of unique) seq.add(keep.get(d) ?? doc.createNode(d))
  m.set('deps', seq)
}

export function addDep(doc: Document, task: string, dep: string) {
  if (task === dep) return
  const current = read(doc).tasks.find(t => t.name === task)?.deps ?? []
  if (!current.includes(dep)) setDeps(doc, task, [...current, dep])
}

export function removeDep(doc: Document, task: string, dep: string) {
  const current = read(doc).tasks.find(t => t.name === task)?.deps ?? []
  setDeps(doc, task, current.filter(d => d !== dep))
}

export function setLabels(doc: Document, task: string, labels: string[]) {
  const m = ensureTaskMap(doc, task)
  const clean = labels.map(l => l.trim()).filter(Boolean)
  if (clean.length) m.setIn(['x-agent', 'labels'], flowList(doc, clean))
  else deleteAndPrune(m, ['x-agent', 'labels'])
}

export function setArtifacts(doc: Document, task: string, artifacts: string[]) {
  const m = ensureTaskMap(doc, task)
  const clean = artifacts.map(a => a.trim()).filter(Boolean)
  if (clean.length) m.set('x-artifacts', flowList(doc, clean))
  else m.delete('x-artifacts')
}

export function setSecrets(doc: Document, task: string, secrets: string[]) {
  const m = ensureTaskMap(doc, task)
  const clean = [...new Set(secrets.map(x => x.trim()).filter(Boolean))]
  if (clean.length) m.set('x-secrets', flowList(doc, clean))
  else m.delete('x-secrets')
}

export function setApproval(doc: Document, task: string, approval: ApprovalModel | null) {
  const m = ensureTaskMap(doc, task)
  if (!approval) { m.delete('x-approval'); return }
  if (!isMap(m.get('x-approval', true))) m.set('x-approval', new YAMLMap())
  if (approval.message.trim()) m.setIn(['x-approval', 'message'], approval.message)
  else m.deleteIn(['x-approval', 'message'])
  const approvers = approval.approvers.map(a => a.trim()).filter(Boolean)
  if (approvers.length) m.setIn(['x-approval', 'approvers'], flowList(doc, approvers))
  else m.deleteIn(['x-approval', 'approvers'])
}

export function setDeploy(doc: Document, task: string, deploy: DeployModel | null) {
  const m = ensureTaskMap(doc, task)
  if (!deploy) { m.delete('x-deploy'); return }
  if (!isMap(m.get('x-deploy', true))) m.set('x-deploy', new YAMLMap())
  for (const k of ['environment', 'compose', 'project', 'manifests', 'namespace', 'url'] as const) {
    const v = deploy[k].trim()
    if (v) m.setIn(['x-deploy', k], v)
    else m.deleteIn(['x-deploy', k])
  }
}

export function setEntry(doc: Document, entry: string) {
  if (entry.trim()) {
    if (!isMap(doc.get('x-builder', true))) doc.set('x-builder', new YAMLMap())
    doc.setIn(['x-builder', 'entry'], entry.trim())
  } else {
    doc.deleteIn(['x-builder', 'entry'])
    const xb = doc.get('x-builder', true)
    if (isMap(xb) && xb.items.length === 0) doc.delete('x-builder')
  }
}

export const TASK_NAME = /^[A-Za-z0-9_][A-Za-z0-9_.:-]*$/

export function addTask(doc: Document, name: string, deps: string[] = []) {
  if (!tasksMap(doc)) doc.set('tasks', new YAMLMap())
  const tasks = tasksMap(doc)!
  if (tasks.has(name)) throw new Error(`Task "${name}" already exists.`)
  const map = new YAMLMap()
  if (deps.length) map.set('deps', flowList(doc, deps))
  map.set('cmds', doc.createNode([`echo "${name}"`]))
  tasks.add(doc.createPair(name, map))
}

/** Renames a task and every reference to it (deps, `- task:` cmds, x-builder.entry). */
export function renameTask(doc: Document, from: string, to: string) {
  const tasks = tasksMap(doc)
  if (!tasks || from === to) return
  if (tasks.has(to)) throw new Error(`Task "${to}" already exists.`)
  const pair = tasks.items.find(p => keyOf(p as Pair) === from) as Pair | undefined
  if (!pair) return
  const key = pair.key
  if (isScalar(key)) key.value = to
  else pair.key = doc.createNode(to)

  for (const p of tasks.items) {
    const node = (p as Pair).value
    if (!isMap(node)) continue
    const deps = node.get('deps', true)
    if (isSeq(deps)) {
      for (const d of deps.items) {
        if (isScalar(d) && d.value === from) d.value = to
        else if (isMap(d) && d.get('task') === from) d.set('task', to)
      }
    }
    const cmds = node.get('cmds', true)
    if (isSeq(cmds)) for (const c of cmds.items) if (isMap(c) && c.get('task') === from) c.set('task', to)
  }
  if (str(doc.getIn(['x-builder', 'entry'])) === from) doc.setIn(['x-builder', 'entry'], to)
}

/** Deletes a task and removes it from other tasks' deps and `- task:` cmds. */
export function deleteTask(doc: Document, name: string) {
  const tasks = tasksMap(doc)
  if (!tasks) return
  tasks.delete(name)
  for (const p of tasks.items) {
    const node = (p as Pair).value
    if (!isMap(node)) continue
    const deps = node.get('deps', true)
    if (isSeq(deps)) {
      deps.items = deps.items.filter(d => !(isScalar(d) && d.value === name) && !(isMap(d) && d.get('task') === name))
      if (!deps.items.length) node.delete('deps')
    }
    const cmds = node.get('cmds', true)
    if (isSeq(cmds)) cmds.items = cmds.items.filter(c => !(isMap(c) && c.get('task') === name))
  }
  if (str(doc.getIn(['x-builder', 'entry'])) === name) setEntry(doc, '')
}

/** Tasks reachable from `entry` through deps and `- task:` cmds (what a build would run). */
export function reachable(model: TaskfileModel, entry: string): Set<string> {
  const byName = new Map(model.tasks.map(t => [t.name, t]))
  const seen = new Set<string>()
  const stack = [entry]
  while (stack.length) {
    const n = stack.pop()!
    if (seen.has(n) || !byName.has(n)) continue
    seen.add(n)
    const t = byName.get(n)!
    stack.push(...t.deps, ...t.cmds.filter(c => c.kind === 'task').map(c => c.text))
  }
  return seen
}

export const STARTER = `version: '3'

x-builder:
  entry: ci

tasks:
  ci:
    desc: Full pipeline
    deps: [test]

  build:
    cmds:
      - echo "building {{.BUILDER_COMMIT}}"

  test:
    deps: [build]
    cmds:
      - echo "testing"
`
