/* eslint-disable */
export function parse(value: unknown): string {
  return typeof value === "string" ? value : "";
}
