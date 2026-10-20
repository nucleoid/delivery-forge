export function parseName(value: unknown): string {
  return typeof value === "string" ? value : "";
}
