declare const format: (value: string) => string;
declare const x: unknown;

export const View = () => (
  <p>
    {format("}")} use ` here
  </p>
);
export const hidden = x as unknown as string[];
// `
