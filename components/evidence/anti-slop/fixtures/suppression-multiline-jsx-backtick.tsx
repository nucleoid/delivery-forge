declare const x: unknown;

export const View = () => (
  <p>
    use ` here
  </p>
);
// eslint-disable-next-line anti-slop/no-chained-type-assertions
export const hidden = x as unknown as string[];
// `
