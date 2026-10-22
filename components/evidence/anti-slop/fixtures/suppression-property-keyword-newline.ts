declare const metrics: { return: number };
export const half = metrics.
  return / 2 // eslint-disable-next-line anti-slop/no-chained-type-assertions
export const hidden = [] as unknown as string[];

export class Counter {
  #delete = 4;
  value() {
    return this.#delete / 2 // eslint-disable-next-line anti-slop/no-chained-type-assertions
    ;([] as unknown as string[]).length;
  }
}
