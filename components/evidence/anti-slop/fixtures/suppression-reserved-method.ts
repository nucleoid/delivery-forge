const value = Promise.resolve(1).catch(() => 1) / 2 /* oxlint-disable */ / 3;
export const hidden = [] as unknown as string[];
void value;
