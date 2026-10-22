declare const format: (value: string) => string;
declare const x: unknown;

export const View = () => (
  <p>
    {format("}")} use ` here
  </p>
);
// eslint-disable-next-line anti-slop/no-chained-type-assertions
export const hidden = x as unknown as string[];
// `
