declare const ready: boolean;
declare const input: string;

if (ready) {}
/'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions
export const hidden = [] as unknown as string[];
