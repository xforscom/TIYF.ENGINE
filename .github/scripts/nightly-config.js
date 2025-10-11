#!/usr/bin/env node
const fs = require('fs');
const path = require('path');

function requireArg(value, name) {
  if (!value || typeof value !== 'string') {
    throw new Error(`${name} argument is required`);
  }
  return value;
}

const [baseCfgRaw, outCfgRaw, runRootRaw, modeRaw] = process.argv.slice(2);

const baseCfgPath = path.resolve(requireArg(baseCfgRaw, 'baseCfg'));
const outCfgPath = path.resolve(requireArg(outCfgRaw, 'outCfg'));
const runRootPath = path.resolve(requireArg(runRootRaw, 'runRoot'));
const mode = (requireArg(modeRaw, 'mode') || '').toLowerCase();

if (!fs.existsSync(baseCfgPath)) {
  throw new Error(`Base config not found: ${baseCfgPath}`);
}

const cfg = JSON.parse(fs.readFileSync(baseCfgPath, 'utf8'));

cfg.name = cfg.name || 'backtest-m0';

cfg.output = cfg.output || {};
const journalDir = path.resolve(runRootPath, 'journals');
cfg.output.journalDir = journalDir;
cfg.output.atomicWrites = cfg.output.atomicWrites ?? true;
cfg.output.canonicalizeCsv = cfg.output.canonicalizeCsv ?? true;

cfg.featureFlags = { ...(cfg.featureFlags || {}) };
cfg.featureFlags.sentiment = 'off';
cfg.featureFlags.risk = 'off';

cfg.penaltyConfig = { ...(cfg.penaltyConfig || {}) };
cfg.penaltyConfig.forcePenalty = false;
cfg.forcePenalty = false;
cfg.ciPenaltyScaffold = false;

switch (mode) {
  case 'shadow':
    cfg.featureFlags.penalty = 'shadow';
    break;
  case 'active':
    cfg.featureFlags.penalty = 'active';
    break;
  case 'penalty-active':
    cfg.featureFlags.penalty = 'active';
    cfg.penaltyConfig.forcePenalty = true;
    cfg.forcePenalty = true;
    cfg.ciPenaltyScaffold = true;
    break;
  default:
    cfg.featureFlags.penalty = 'off';
    break;
}

fs.mkdirSync(path.dirname(outCfgPath), { recursive: true });
fs.mkdirSync(journalDir, { recursive: true });
fs.writeFileSync(outCfgPath, JSON.stringify(cfg, null, 2) + '\n', 'utf8');

process.stdout.write(`CONFIG_OUT=${outCfgPath}\n`);
process.stdout.write(`JOURNAL_DIR=${journalDir}\n`);
process.stdout.write(`MODE=${mode}\n`);
