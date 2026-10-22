declare const input: string;

export async function run() {
  await /'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
  return [] as unknown as string[];
}
