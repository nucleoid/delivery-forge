#!/usr/bin/env node
import { getScenario } from '../src/fixtures.mjs';
import { renderExpanded } from '../src/render-text.mjs';
const name=process.argv[2]||'failed-gate',width=Number(process.argv[3])||80;
process.stdout.write(`${renderExpanded(getScenario(name).snapshot,{width})}\n`);
