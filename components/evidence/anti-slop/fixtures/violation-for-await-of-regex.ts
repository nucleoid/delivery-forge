export async function read(input: string) {
  for await (const match of /x'/.exec(input)) { // matched unsuppressed control -- '
    const hidden = [] as unknown as string[]
    void match
    return hidden
  }
  return []
}
