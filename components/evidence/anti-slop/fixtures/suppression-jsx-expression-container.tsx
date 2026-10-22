declare const x: string;

export const view = <div>{x}'</div>, hidden = [] as unknown as string[]; /* ' */ // eslint-disable-line anti-slop/no-chained-type-assertions
