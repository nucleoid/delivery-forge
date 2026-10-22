const values = [1, 2, 3];

// eslint-disable-next-line eslint/no-accumulating-spread
export const copied = values.reduce((accumulator, value) => [...accumulator, value], new Array<number>());
