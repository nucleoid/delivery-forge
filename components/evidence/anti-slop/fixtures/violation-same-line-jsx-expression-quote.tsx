declare const x: unknown;

export const View = () => <p>{x as unknown as string[]} x 'y</p>;
