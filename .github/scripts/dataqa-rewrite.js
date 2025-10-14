#!/usr/bin/env node
const fs = require('fs');
const path = require('path');

function ensure(value, message) {
  if (!value) {
    throw new Error(message);
  }
  return value;
}

const configIn = ensure(process.env.BASE_CFG, 'BASE_CFG env required');
const configOut = ensure(process.env.CFG_OUT, 'CFG_OUT env required');
const runRoot = ensure(process.env.RUN_ROOT, 'RUN_ROOT env required');
const mode = (process.env.K_MODE || '').toLowerCase();
const extraMissingRaw = Number(process.env.DQ_EXTRA_MISSING || '0');
const isOverflow = mode === 'overflow';

const cfg = JSON.parse(fs.readFileSync(configIn, 'utf8'));

cfg.output = cfg.output || {};
cfg.output.journalDir = path.resolve(runRoot, 'journals');

cfg.featureFlags = { ...(cfg.featureFlags || {}) };
cfg.featureFlags.dataQa = 'active';

cfg.dataQaConfig = { ...(cfg.dataQaConfig || {}) };
cfg.dataQaConfig.maxMissingBarsPerInstrument = 1;
const toleranceRaw = Number(cfg.dataQaConfig.maxMissingBarsPerInstrument ?? 0);
const tolerance = Number.isFinite(toleranceRaw) ? toleranceRaw : 0;
const extraMissing = Number.isFinite(extraMissingRaw) ? extraMissingRaw : 0;

const qaDefaults = isOverflow
  ? {
      enabled: true,
      maxMissingBarsPerInstrument: Math.max(0, Math.floor(tolerance)),
      allowDuplicates: false,
      spikeZ: 5,
      repair: { forwardFillBars: 0, dropSpikes: true }
    }
  : {
      enabled: true,
      maxMissingBarsPerInstrument: 999,
      allowDuplicates: true,
      spikeZ: 50,
      repair: { forwardFillBars: 1, dropSpikes: false }
    };

const currentQa = cfg.dataQA && typeof cfg.dataQA === 'object' ? cfg.dataQA : {};
cfg.dataQA = {
  ...currentQa,
  ...qaDefaults
};

const data = cfg.data = cfg.data || {};
const ticks = data.ticks || {};
const symbols = Object.keys(ticks);
const targetSymbol = isOverflow
  ? (symbols.find((s) => /EURUSD/i.test(s)) || symbols[0] || null)
  : null;

const ticksDir = path.resolve(runRoot, 'ticks');
fs.mkdirSync(ticksDir, { recursive: true });

const outTicks = {};

for (const [sym, srcRaw] of Object.entries(ticks)) {
  if (typeof srcRaw !== 'string' || srcRaw.length === 0) continue;
  const absSrc = path.resolve(srcRaw);
  if (!fs.existsSync(absSrc)) {
    throw new Error(`Tick file not found: ${absSrc}`);
  }

  const lines = fs.readFileSync(absSrc, 'utf8').split(/\r?\n/);
  if (lines.length === 0) continue;
  const header = lines.shift();
  const rows = lines.filter((line) => line.trim().length > 0);
  if (rows.length === 0) {
    continue;
  }

  let rowsAfter = rows.slice();

  const maxLeadingRemovable = Math.max(0, rowsAfter.length - 1);
  const leadingRemove = Math.min(Math.max(0, Math.floor(tolerance)), maxLeadingRemovable);
  if (leadingRemove > 0) {
    rowsAfter = rowsAfter.slice(leadingRemove);
    console.log(`[dataqa:${mode || 'pass'}] ${sym}: removed ${leadingRemove} leading rows (${rows.length} -> ${rowsAfter.length})`);
  }

  if (isOverflow && sym === targetSymbol) {
    const parsed = rowsAfter.map((line) => {
      const parts = line.split(',');
      const ts = parts[0];
      return {
        minuteKey: ts.slice(0, 16), // YYYY-MM-DDTHH:MM
        line
      };
    });
    const minuteOrder = [];
    const seenMinutes = new Set();
    for (const { minuteKey } of parsed) {
      if (!seenMinutes.has(minuteKey)) {
        seenMinutes.add(minuteKey);
        minuteOrder.push(minuteKey);
      }
    }
    const availableGap = Math.max(0, minuteOrder.length - 1);
    const minutesToDrop = Math.min(Math.max(0, Math.floor(extraMissing)), availableGap);
    if (minutesToDrop > 0) {
      let startMinuteIdx = Math.floor(minuteOrder.length / 2) - Math.floor(minutesToDrop / 2);
      startMinuteIdx = Math.max(1, startMinuteIdx);
      if (startMinuteIdx + minutesToDrop >= minuteOrder.length) {
        startMinuteIdx = Math.max(1, minuteOrder.length - minutesToDrop - 1);
      }
      const dropSet = new Set();
      for (let i = 0; i < minutesToDrop; i++) {
        dropSet.add(minuteOrder[startMinuteIdx + i]);
      }
      const beforeCount = rowsAfter.length;
      rowsAfter = parsed.filter((entry) => !dropSet.has(entry.minuteKey)).map((entry) => entry.line);
      console.log(`[dataqa:${mode}] ${sym}: removed ${beforeCount - rowsAfter.length} rows across ${dropSet.size} minute buckets starting at index ${startMinuteIdx}`);
    }
  }

  if (rowsAfter.length === 0 && rows.length > 0) {
    rowsAfter = [rows[rows.length - 1]];
  }

  const dst = path.resolve(ticksDir, `ticks_${sym}.csv`);
  fs.writeFileSync(dst, [header, ...rowsAfter].join('\n') + '\n', 'utf8');

  const verify = fs.readFileSync(dst, 'utf8').trim().split(/\r?\n/);
  if (verify.length < 2) {
    throw new Error(`After rewrite ${dst} has no data rows (lines=${verify.length})`);
  }

  outTicks[sym] = dst;
}

data.ticks = outTicks;

fs.mkdirSync(path.dirname(configOut), { recursive: true });
fs.writeFileSync(configOut, JSON.stringify(cfg, null, 2) + '\n', 'utf8');
