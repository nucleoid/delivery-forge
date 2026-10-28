#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { snapshotsFromCatalog } from '../src/fixtures.mjs';
import { project } from '../src/projector.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const states=snapshotsFromCatalog().map(({name,snapshot})=>({name,...project(snapshot)}));
const banner='// GENERATED from data/fixtures.json by scripts/build-companion-data.mjs; do not hand-edit.\n';
fs.writeFileSync(path.join(root,'companion/companion-data.js'),`${banner}globalThis.FORGE_DEMO_STATES=${JSON.stringify(states)};\n`);
