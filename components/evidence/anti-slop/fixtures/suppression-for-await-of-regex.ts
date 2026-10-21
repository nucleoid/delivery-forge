export async function read(input: string) {
  for await (const match of /x'/.exec(input)) { // eslint-disable-next-line anti-slop/no-chained-type-assertions -- '
    const hidden = [] as unknown as string[]
    void match
    return hidden
  }
  return []
}
