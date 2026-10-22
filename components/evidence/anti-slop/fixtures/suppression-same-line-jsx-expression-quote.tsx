declare const x: unknown;

export const View = () => <p>{x as unknown as string[]} x 'y</p>; // eslint-disable-line anti-slop/no-chained-type-assertions -- '
