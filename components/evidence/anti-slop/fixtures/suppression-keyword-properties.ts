const of = 4;
export const hiddenOf = [] as unknown as string[]; void (of / 2); // eslint-disable-line

const object = { in: 8, delete: 10, return: 12 };
export const hiddenIn = [] as unknown as string[]; void (object.in / 2); // eslint-disable-line

export const hiddenDelete = [] as unknown as string[]; void (object.delete / 2); // eslint-disable-line

export const hiddenReturn = [] as unknown as string[]; void (object.return / 2); // eslint-disable-line
