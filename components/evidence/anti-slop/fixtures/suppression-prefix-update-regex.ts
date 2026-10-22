const incremented = ++/'/.lastIndex /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
export const first = [] as unknown as string[];
const decremented = --/'/.lastIndex /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
export const second = [] as unknown as string[];
void incremented;
void decremented;
