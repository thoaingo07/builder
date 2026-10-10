import { Document, isMap, isScalar, isSeq, parseDocument, YAMLMap, type Pair } from 'yaml'
type YamlMap = YAMLMap

/**
 * Read-only model of a go-task Taskfile with Builder's x- extensions.
 * Builder never writes runner files: they are changed in the repository.
 */

export interface CmdModel { kind: 'cmd' | 'task' | 'other'; text: string }
/** One entry of a task's `cmds` (a build step). */
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
  /** uses keys beyond cmd/task/vars/silent/ignore_error/defer (for:, platforms:, …): shown as raw YAML */
  complex: boolean
  /** YAML text of the step */
  raw: string
}
/** `vars:` / `env:` entry; dynamic = a computed (`sh:`) value. */
export interface KeyValue { key: string; value: string; dynamic?: boolean }
/** go-task `requires.vars` entry */
export interface RequiredVar { name: string; enum: string[] }

export interface ApprovalModel { message: string; approvers: string[] }
/** x-registries entry: a registry host, optionally logged in through a named connection. */
export interface RegistryModel { registry: string; connection: string }
export interface ContainerModel {
  strategy: 'BlueGreen' | 'Recreate'; service: string; image: string; network: string; envFile: string | null
  args: string[]; command: string[] | null
  healthPath: string | null; healthPort: number | null; healthScheme: string; timeoutSeconds: number; keep: number
}
export interface DeployModel {
  environment: string; compose: string; project: string; manifests: string; namespace: string; url: string
  /** x-deploy.strategy (blue-green / recreate): a single container behind a network alias */
  container: ContainerModel | null
}

export interface TaskModel {
  name: string
  desc: string
  deps: string[]
  /** deps written as `{ task, vars }` maps — editable only in YAML */
  hasComplexDeps: boolean
  cmds: CmdModel[]
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

/** push / pull-request trigger; branches [] = all branches (PR: target branches) */
export interface FilterTriggerModel { branches: string[]; paths: string[]; vars: KeyValue[] }
export interface ScheduleModel { cron: string; branch: string; timeZone: string; vars: KeyValue[] }
export interface TriggersModel {
  push: FilterTriggerModel | null
  pullRequest: FilterTriggerModel | null
  schedules: ScheduleModel[]
  /** x-builder.triggers has a shape Builder can't read */
  error: string | null
}

export interface TaskfileModel {
  version: string
  entry: string
  triggers: TriggersModel
  tasks: TaskModel[]
}

export function parse(content: string): Document {
  return parseDocument(content, { prettyErrors: true, keepSourceTokens: true })
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
    name, desc: '', deps: [], hasComplexDeps: false, cmds: [],
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
        container: readContainer(deploy),
      }
    }
  }
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

/** Mirrors the server's reading of x-deploy.strategy (defaults: timeout 120 s, keep 1, scheme http). */
function readContainer(d: YamlMap): ContainerModel | null {
  const strategy = str(d.get('strategy')).toLowerCase()
  if (!strategy) return null
  const health = d.get('health', true)
  const h = (k: string) => (isMap(health) ? str(health.get(k)) : '')
  const num = (v: string) => (/^\d+$/.test(v) ? Number(v) : null)
  const command = stringList(d.get('command', true))
  return {
    strategy: strategy === 'recreate' ? 'Recreate' : 'BlueGreen',
    service: str(d.get('service')), image: str(d.get('image')), network: str(d.get('network')),
    envFile: str(d.get('env-file')) || null, args: stringList(d.get('args', true)), command: command.length ? command : null,
    healthPath: h('path') || null, healthPort: num(h('port')), healthScheme: h('scheme') || 'http',
    timeoutSeconds: num(h('timeout').replace(/s$/, '')) ?? 120, keep: num(str(d.get('keep'))) ?? 1,
  }
}

const STEP_KEYS: Record<StepKindModel, string[]> = {
  cmd: ['cmd', 'silent', 'ignore_error'],
  task: ['task', 'vars', 'silent', 'ignore_error'],
  defer: ['defer'],
}

function blankStep(kind: StepKindModel): StepModel {
  return { kind, cmd: '', task: '', vars: [], silent: false, ignoreError: false, complex: false, raw: '' }
}

function readStep(n: unknown): StepModel {
  const raw = n === null || n === undefined ? '' : String(n).trim()
  if (isScalar(n)) return { ...blankStep('cmd'), cmd: str(n.value), raw }
  if (!isMap(n)) return { ...blankStep('cmd'), complex: true, raw }
  const keys = n.items.map(p => keyOf(p as Pair))
  const kind: StepKindModel | null = n.has('task') ? 'task' : n.has('defer') ? 'defer' : n.has('cmd') ? 'cmd' : null
  if (!kind) return { ...blankStep('cmd'), complex: true, raw }
  const step: StepModel = {
    ...blankStep(kind), raw,
    silent: scalarValue(n.get('silent', true)) === true,
    ignoreError: scalarValue(n.get('ignore_error', true)) === true,
  }
  let complex = keys.some(k => !STEP_KEYS[kind].includes(k))
  if (kind === 'cmd') {
    const c = n.get('cmd', true)
    if (isScalar(c)) step.cmd = str(c.value); else complex = true
  } else if (kind === 'defer') {
    const d = n.get('defer', true)
    if (isScalar(d)) step.cmd = str(d.value); else complex = true   // `defer: { task: x }` → YAML only
  } else {
    step.task = str(n.get('task'))
    const vars = readKeyValues(n.get('vars', true))
    if (vars.some(v => v.dynamic)) complex = true
    step.vars = vars
  }
  step.complex = complex
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

const TRUE = new Set(['true', 'on', 'yes'])
const FALSE = new Set(['false', 'off', 'no'])

/** `true` → all branches; "main" / [main, release/*] → branches; { branches, paths, vars } → full; false/absent → off */
function readFilter(n: unknown): FilterTriggerModel | null {
  if (n === undefined || n === null) return null
  if (isScalar(n)) {
    const v = String(n.value ?? '').toLowerCase()
    if (n.value === false || FALSE.has(v)) return null
    if (n.value === true || TRUE.has(v)) return { branches: [], paths: [], vars: [] }
    return { branches: [str(n.value)], paths: [], vars: [] }
  }
  if (isSeq(n)) return { branches: stringList(n), paths: [], vars: [] }
  if (isMap(n)) return { branches: stringList(n.get('branches', true)), paths: stringList(n.get('paths', true)), vars: readKeyValues(n.get('vars', true)) }
  return null
}

function triggersNode(doc: Document): YAMLMap | null {
  const t = doc.getIn(['x-builder', 'triggers'], true)
  return isMap(t) ? t : null
}

function readTriggers(doc: Document): TriggersModel {
  const model: TriggersModel = { push: null, pullRequest: null, schedules: [], error: null }
  const raw = doc.getIn(['x-builder', 'triggers'], true)
  if (raw === undefined || raw === null) return model
  const t = triggersNode(doc)
  if (!t) return { ...model, error: 'x-builder.triggers must be a mapping (push, pull-request, schedule).' }
  model.push = readFilter(t.get('push', true))
  model.pullRequest = readFilter(t.has('pull-request') ? t.get('pull-request', true) : t.get('pr', true))
  const sched = t.get('schedule', true)
  const items = isSeq(sched) ? sched.items : isMap(sched) ? [sched] : []
  model.schedules = items.filter(isMap).map(m => ({
    cron: str(m.get('cron')), branch: str(m.get('branch')),
    timeZone: str(m.get('timezone')) || 'UTC', vars: readKeyValues(m.get('vars', true)),
  }))
  return model
}

export function read(doc: Document): TaskfileModel {
  const tasks = tasksMap(doc)
  return {
    version: str(doc.get('version')),
    entry: str(doc.getIn(['x-builder', 'entry'])),
    triggers: readTriggers(doc),
    tasks: tasks ? tasks.items.map(p => readTask(keyOf(p as Pair), (p as Pair).value)) : [],
  }
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
