/** Minimal ANSI SGR parser: turns colored terminal output into styled segments. */
export interface AnsiSegment { text: string; cls: string }

const FG: Record<number, string> = {
  30: 'black', 31: 'red', 32: 'green', 33: 'yellow', 34: 'blue', 35: 'magenta', 36: 'cyan', 37: 'white',
  90: 'gray', 91: 'red', 92: 'green', 93: 'yellow', 94: 'blue', 95: 'magenta', 96: 'cyan', 97: 'white',
}

// eslint-disable-next-line no-control-regex
const SGR = /\x1b\[([0-9;]*)m/g
// eslint-disable-next-line no-control-regex
const OTHER_ESC = /\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07|\r/g

export function parseAnsi(input: string): AnsiSegment[] {
  if (!input.includes('\x1b')) return [{ text: input.replace(/\r/g, ''), cls: '' }]
  const out: AnsiSegment[] = []
  let fg = ''
  let bold = false
  let last = 0
  const push = (text: string) => {
    const clean = text.replace(OTHER_ESC, '')
    if (!clean) return
    const cls = [fg && `ansi-${fg}`, bold && 'ansi-bold'].filter(Boolean).join(' ')
    out.push({ text: clean, cls })
  }
  for (const m of input.matchAll(SGR)) {
    push(input.slice(last, m.index))
    last = (m.index ?? 0) + m[0].length
    const codes = (m[1] || '0').split(';').map(Number)
    for (const c of codes) {
      if (c === 0) { fg = ''; bold = false }
      else if (c === 1) bold = true
      else if (c === 22) bold = false
      else if (c === 39) fg = ''
      else if (FG[c]) fg = FG[c]
    }
  }
  push(input.slice(last))
  return out
}
