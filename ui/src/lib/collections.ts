/** Replace the item with the same id (keeping position) or insert it at the front. Returns a new array. */
export function upsert<T extends { id: string }>(list: T[], item: T, insertAtFront = true): T[] {
  const i = list.findIndex(x => x.id === item.id)
  if (i >= 0) return list.map((x, j) => (j === i ? item : x))
  return insertAtFront ? [item, ...list] : [...list, item]
}

export function debounce<A extends unknown[]>(fn: (...args: A) => void, ms: number) {
  let t: number | undefined
  return (...args: A) => {
    window.clearTimeout(t)
    t = window.setTimeout(() => fn(...args), ms)
  }
}
