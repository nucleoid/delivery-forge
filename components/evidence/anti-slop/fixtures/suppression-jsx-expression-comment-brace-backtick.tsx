declare const format: (value: unknown) => string;
declare const value: unknown;
declare const x: unknown;

export const View = () => (
  <p>
    {/* } */ format(value)} use ` here
  </p>
);
// eslint-disable-next-line anti-slop/no-chained-type-assertions
export const hidden = x as unknown as string[];
// `
