const values = [1, 2, 3];

export const copied = values.reduce((accumulator, value) => [...accumulator, value], new Array<number>());
