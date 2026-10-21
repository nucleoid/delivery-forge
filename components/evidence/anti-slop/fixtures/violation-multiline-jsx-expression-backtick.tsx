declare const name: string;
declare const x: unknown;

export const View = () => (
  <p>
    {name} use ` here
  </p>
);
export const hidden = x as unknown as string[];
// `
