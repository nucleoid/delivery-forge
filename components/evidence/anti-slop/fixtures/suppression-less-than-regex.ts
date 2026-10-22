declare const a: number;
const ok = a</'/.source.length /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
export const hidden = [] as unknown as string[];
void ok;
