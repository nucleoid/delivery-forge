declare function check(value: string): void;

export function returned(value: string) {
  check(value)
  return /'/.test(value) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
    && ([] as unknown as string[]).length > 0
}

export function thrown(value: string) {
  check(value)
  throw /'/.test(value) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
    && ([] as unknown as string[]).length > 0
}

export function* yielded(value: string) {
  check(value)
  yield /'/.test(value) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
    && ([] as unknown as string[]).length > 0
}

export async function awaited(value: string) {
  check(value)
  await /'/.test(value) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
    && ([] as unknown as string[]).length > 0
}
