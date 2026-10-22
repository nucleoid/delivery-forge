declare const x: unknown;

export const View = () => (
  <p>
    use ` here
  </p>
);
// matched unsuppressed control
export const hidden = x as unknown as string[];
// `
