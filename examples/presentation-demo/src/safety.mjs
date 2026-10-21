const CONTROL = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/g;
export const cleanText = (value, max = 4096) => String(value ?? '').replace(CONTROL, '�').slice(0, max);
export const stripAnsi = (value) => String(value).replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '');
const combining = /\p{Mark}/u;
const wide = /[\u1100-\u115f\u2329\u232a\u2e80-\ua4cf\uac00-\ud7a3\uf900-\ufaff\ufe10-\ufe19\ufe30-\ufe6f\uff00-\uff60\uffe0-\uffe6\u{1f300}-\u{1faff}\u{20000}-\u{3fffd}]/u;
const segments = (value) => {
  const plain = stripAnsi(value);
  if (typeof Intl.Segmenter === 'function') return [...new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(plain)].map((x) => x.segment);
  return [...plain];
};
const cellWidth = (segment) => [...segment].every((ch) => combining.test(ch)) ? 0 : wide.test(segment) ? 2 : 1;
export const displayWidth = (value) => segments(value).reduce((sum, segment) => sum + cellWidth(segment), 0);
export function sliceByColumns(value, width) {
  const limit = Math.max(0, Number(width) || 0); let used = 0, out = '';
  for (const segment of segments(value)) { const cells = cellWidth(segment); if (used + cells > limit) break; out += segment; used += cells; }
  return out;
}
export const truncateToWidth = (value, width, ellipsis = '…') => displayWidth(value) <= width ? String(value) : sliceByColumns(value, Math.max(0, width - displayWidth(ellipsis))) + ellipsis;
export const htmlEscape = (value) => cleanText(value).replace(/[&<>'"]/g, (c) => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
export const short = (value, size = 7) => cleanText(value).slice(0, size);

export function wrap(text, width, prefix = '') {
  const safeWidth = Math.max(20, Math.min(120, Number(width) || 80));
  const available = Math.max(8, safeWidth - displayWidth(prefix));
  const words = cleanText(text).split(/\s+/).filter(Boolean);
  const lines = [];
  let line = '';
  for (const word of words) {
    if (!line) line = word;
    else if (displayWidth(`${line} ${word}`) <= available) line += ` ${word}`;
    else { lines.push(prefix + line); line = word; }
  }
  if (line || !lines.length) lines.push(prefix + line);
  return lines;
}

export function boundLines(lines, width) {
  const safeWidth = Math.max(20, Math.min(120, Number(width) || 80));
  return lines.flatMap((line) => displayWidth(line) <= safeWidth ? [line] : wrap(line, safeWidth));
}
