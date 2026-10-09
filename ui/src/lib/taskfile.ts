import { Document, isMap, isScalar, isSeq, parseDocument, Scalar, YAMLMap, YAMLSeq, type Node, type Pair } from 'yaml'

/**
 * Read model + comment-preserving mutations for go-task Taskfiles with Builder's x- extensions.
 * All writes go through the `yaml` Document so formatting and comments elsewhere survive.
 */

export interface CmdModel { kind: 'cmd' | 'task' | 'other'; text: string }
/** One entry of a task's `cmds`, as the step editor sees it. */
export type StepKindModel = 'cmd' | 'task' | 'defer'
export interface StepModel {
  kind: StepKindModel
  /** command text (cmd / defer) */
  cmd: string
  /** called task (kind = task) */
  task: string
  vars: KeyValue[]
  silent: boolean
  ignoreError: boolean
  /** has keys the editor doesn't know (for:, platforms:, …) or non-scalar values: edit in YAML */
  readonly: boolean
  /** YAML text of the step, shown for read-only steps */
  raw: string
}
/** `vars:` / `env:` entry; dynamic = a `sh:`/map value the form can't edit (kept as-is). */
export interface KeyValue { key: string; value: string; dynamic?: boolean }
/** go-task `requires.vars` entry */
export interface RequiredVar { name: string; enum: string[] }

export interface ApprovalModel { message: string; approvers: string[] }
/** x-registries entry: a registry host, optionally logged in through a named connection. */
export interface RegistryModel { registry: string; connection: string }
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
  steps: StepModel[]
  vars: KeyValue[]
  env: KeyValue[]
  requires: RequiredVar[]
  /** x-registries: container registries the job logs in to */
  registries: RegistryModel[]
  /** x-azure-artifacts: hand the job an Azure Artifacts token */
  azureArtifacts: boolean
  approval: ApprovalModel | null
  deploy: DeployModel | null
}

export interface TaskfileModel {
  version: string
  entry: string
  tasks: TaskModel[]
}

export function parse(content: string): Document {
  return parseDocument(content, { prettyErrors: true, keepSourceTokens: true })
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
    labels: [], artifacts: [], secrets: [], steps: [], vars: [], env: [], requires: [], registries: [], azureArtifacts: false, approval: null, deploy: null,
  }
  if (isSeq(node)) {
    t.cmds = node.items.map(readCmd)
    t.steps = node.items.map(readStep)
  } else if (isScalar(node)) {
    if (node.value !== null && node.value !== '') {
      t.cmds = [{ kind: 'cmd', text: str(node.value) }]
      t.steps = [readStep(node)]
    }
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
    t.steps = isSeq(cmds) ? cmds.items.map(readStep) : node.has('cmd') ? [readStep(node.get('cmd', true))] : []
    t.vars = readKeyValues(node.get('vars', true))
    t.env = readKeyValues(node.get('env', true))
    const reqVars = node.getIn(['requires', 'vars'], true)
    if (isSeq(reqVars)) {
      t.requires = reqVars.items.map(r => isMap(r)
        ? { name: str(r.get('name')), enum: stringList(r.get('enum', true)) }
        : { name: str(scalarValue(r)), enum: [] }).filter(r => r.name)
    }

    const agent = node.get('x-agent', true)
    if (isMap(agent)) t.labels = stringList(agent.get('labels', true))
    t.artifacts = stringList(node.get('x-artifacts', true))
    t.secrets = stringList(node.get('x-secrets', true))
    const regs = node.get('x-registries', true)
    if (isSeq(regs)) {
      t.registries = regs.items.map(r => isMap(r)
        ? { registry: str(r.get('registry')), connection: str(r.get('connection')) }
        : { registry: str(scalarValue(r)), connection: '' }).filter(r => r.registry)
    }
    t.azureArtifacts = scalarValue(node.get('x-azure-artifacts', true)) === true

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

const STEP_KEYS: Record<StepKindModel, string[]> = {
  cmd: ['cmd', 'silent', 'ignore_error'],
  task: ['task', 'vars', 'silent', 'ignore_error'],
  defer: ['defer'],
}

function blankStep(kind: StepKindModel): StepModel {
  return { kind, cmd: '', task: '', vars: [], silent: false, ignoreError: false, readonly: false, raw: '' }
}

function readStep(n: unknown): StepModel {
  const raw = n === null || n === undefined ? '' : String(n).trim()
  if (isScalar(n)) return { ...blankStep('cmd'), cmd: str(n.value), raw }
  if (!isMap(n)) return { ...blankStep('cmd'), readonly: true, raw }
  const keys = n.items.map(p => keyOf(p as Pair))
  const kind: StepKindModel | null = n.has('task') ? 'task' : n.has('defer') ? 'defer' : n.has('cmd') ? 'cmd' : null
  if (!kind) return { ...blankStep('cmd'), readonly: true, raw }
  const step: StepModel = {
    ...blankStep(kind), raw,
    silent: scalarValue(n.get('silent', true)) === true,
    ignoreError: scalarValue(n.get('ignore_error', true)) === true,
  }
  let readonly = keys.some(k => !STEP_KEYS[kind].includes(k))
  if (kind === 'cmd') {
    const c = n.get('cmd', true)
    if (isScalar(c)) step.cmd = str(c.value); else readonly = true
  } else if (kind === 'defer') {
    const d = n.get('defer', true)
    if (isScalar(d)) step.cmd = str(d.value); else readonly = true   // `defer: { task: x }` → YAML only
  } else {
    step.task = str(n.get('task'))
    const vars = readKeyValues(n.get('vars', true))
    if (vars.some(v => v.dynamic)) readonly = true
    step.vars = vars
  }
  step.readonly = readonly
  return step
}

/** Scalar text as written (`1.0` stays `1.0`, not the number 1). */
function scalarText(n: unknown): string {
  if (!isScalar(n)) return str(n)
  return typeof n.value === 'string' ? n.value : (n.source ?? str(n.value))
}

function readKeyValues(n: unknown): KeyValue[] {
  if (!isMap(n)) return []
  return n.items.map(p => {
    const pair = p as Pair
    const v = pair.value
    if (isScalar(v) || v === null) return { key: keyOf(pair), value: scalarText(v) }
    const sh = isMap(v) ? v.get('sh') : undefined
    return { key: keyOf(pair), value: sh !== undefined ? `sh: ${str(sh)}` : String(v).trim(), dynamic: true }
  })
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

/** The task's `cmds` sequence, converting a single `cmd:` into it. */
function cmdsSeq(doc: Document, task: string): YAMLSeq {
  const m = ensureTaskMap(doc, task)
  const cmds = m.get('cmds', true)
  if (isSeq(cmds)) return cmds
  const seq = new YAMLSeq()
  if (m.has('cmd')) { seq.add(m.get('cmd', true)); m.delete('cmd') }
  m.set('cmds', seq)
  return seq
}

function stepNode(doc: Document, s: StepModel): unknown {
  if (s.kind === 'defer') return doc.createNode({ defer: s.cmd })
  if (s.kind === 'task') {
    const map = new YAMLMap()
    map.set('task', s.task)
    const vars = s.vars.filter(v => v.key.trim())
    if (vars.length) {
      const vm = doc.createNode(Object.fromEntries(vars.map(v => [v.key.trim(), v.value]))) as YAMLMap
      vm.flow = vars.length <= 3
      map.set('vars', vm)
    }
    if (s.silent) map.set('silent', true)
    if (s.ignoreError) map.set('ignore_error', true)
    return map
  }
  const text = s.cmd.includes('\n') ? (() => { const sc = doc.createNode(s.cmd) as Scalar; sc.type = 'BLOCK_LITERAL'; return sc })() : doc.createNode(s.cmd)
  if (!s.silent && !s.ignoreError) return text
  const map = new YAMLMap()
  map.set('cmd', text)
  if (s.silent) map.set('silent', true)
  if (s.ignoreError) map.set('ignore_error', true)
  return map
}

/** Replaces an editable step. Read-only steps are never passed here, so no unknown keys are lost. */
export function setStep(doc: Document, task: string, index: number, step: StepModel) {
  const seq = cmdsSeq(doc, task)
  if (index < 0 || index >= seq.items.length) return
  seq.items[index] = stepNode(doc, step) as never
}

export function addStep(doc: Document, task: string, kind: StepKindModel, taskName = '') {
  const seq = cmdsSeq(doc, task)
  const step = { ...blankStep(kind), cmd: kind === 'defer' ? 'echo cleanup' : kind === 'cmd' ? 'echo "step"' : '', task: taskName }
  seq.add(stepNode(doc, step))
}

export function removeStep(doc: Document, task: string, index: number) {
  const seq = cmdsSeq(doc, task)
  seq.items.splice(index, 1)
  if (!seq.items.length) ensureTaskMap(doc, task).delete('cmds')
}

/** Moves a step node as-is (keeps comments and unknown keys). */
export function moveStep(doc: Document, task: string, from: number, to: number) {
  const seq = cmdsSeq(doc, task)
  if (to < 0 || to >= seq.items.length || from === to) return
  const [item] = seq.items.splice(from, 1)
  seq.items.splice(to, 0, item)
}

/** Writes `vars:` / `env:`; dynamic (`sh:`) values are kept as their original nodes. */
export function setKeyValues(doc: Document, task: string, field: 'vars' | 'env', entries: KeyValue[]) {
  const m = ensureTaskMap(doc, task)
  const old = m.get(field, true)
  const clean = entries.filter(e => e.key.trim())
  if (!clean.length) { m.delete(field); return }
  const map = new YAMLMap()
  for (const e of clean) {
    const prev = isMap(old) ? old.get(e.key, true) : undefined
    // keep the original node when it is dynamic or unchanged (formatting, comments, `1.0` stay as written)
    const keep = prev !== undefined && (e.dynamic || (isScalar(prev) && scalarText(prev) === e.value))
    map.set(e.key.trim(), keep ? prev : doc.createNode(e.value))
  }
  m.set(field, map)
}

/** Writes `requires.vars`: a plain name, or `{ name, enum }` when values are restricted. */
export function setRequires(doc: Document, task: string, vars: RequiredVar[]) {
  const m = ensureTaskMap(doc, task)
  const clean = vars.map(v => ({ name: v.name.trim(), enum: v.enum.map(e => e.trim()).filter(Boolean) })).filter(v => v.name)
  if (!clean.length) { deleteAndPrune(m, ['requires', 'vars']); return }
  if (!isMap(m.get('requires', true))) m.set('requires', new YAMLMap())
  const seq = new YAMLSeq()
  for (const v of clean) {
    if (!v.enum.length) { seq.add(doc.createNode(v.name)); continue }
    const item = doc.createNode({ name: v.name, enum: v.enum }) as YAMLMap
    item.flow = true
    seq.add(item)
  }
  m.setIn(['requires', 'vars'], seq)
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

/** Writes `x-registries` as `{ registry, connection }` maps (connection omitted when empty). */
export function setRegistries(doc: Document, task: string, registries: RegistryModel[]) {
  const m = ensureTaskMap(doc, task)
  const clean = registries.map(r => ({ registry: r.registry.trim(), connection: r.connection.trim() })).filter(r => r.registry)
  if (!clean.length) { m.delete('x-registries'); return }
  const seq = new YAMLSeq()
  for (const r of clean) {
    const item = doc.createNode(r.connection ? { registry: r.registry, connection: r.connection } : { registry: r.registry }) as YAMLMap
    item.flow = true
    seq.add(item)
  }
  m.set('x-registries', seq)
}

export function setAzureArtifacts(doc: Document, task: string, enabled: boolean) {
  const m = ensureTaskMap(doc, task)
  if (enabled) m.set('x-azure-artifacts', true)
  else m.delete('x-azure-artifacts')
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
