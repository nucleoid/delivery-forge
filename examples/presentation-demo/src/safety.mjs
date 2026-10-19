const CONTROL = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/g;
export const cleanText = (value, max = 4096) => String(value ?? '').replace(CONTROL, '�').slice(0, max);
export const stripAnsi = (value) => String(value).replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '');
export const displayWidth = (value) => [...stripAnsi(value)].length;
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
