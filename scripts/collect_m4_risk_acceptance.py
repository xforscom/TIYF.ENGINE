import json, subprocess, pathlib, hashlib, sys, os, re

ROOT = pathlib.Path(__file__).resolve().parents[1]
SIM_PROJ = ROOT / 'src' / 'TiYf.Engine.Sim'
ART = ROOT / 'acceptance_tmp'
BASE = ROOT / 'tests' / 'fixtures' / 'backtest_m0' / 'config.backtest-m0.json'
ART.mkdir(parents=True, exist_ok=True)

def load(p: pathlib.Path):
    return json.loads(p.read_text(encoding='utf-8'))

def dump(p: pathlib.Path, obj):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(obj, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')

def mutate(mode: str, breach=False, no_breach=False):
    cfg = load(BASE)
    ff = cfg.setdefault('featureFlags', {})
    ff['risk'] = mode
    rc = cfg.setdefault('riskConfig', {})
    if mode in ('shadow','active'):
        rc['emitEvaluations'] = True
        if mode == 'active':
            rc['blockOnBreach'] = True
    if no_breach:
        rc['maxNetExposureBySymbol'] = {'EURUSD': 999999999}
        rc['maxRunDrawdownCCY'] = 999999999.0
    if breach:
        rc['maxNetExposureBySymbol'] = {'EURUSD': 0}
        rc['maxRunDrawdownCCY'] = 999999999.0
    return cfg

def run_sim(cfg_path: pathlib.Path, run_id: str):
    cmd = ['dotnet','run','-c','Release','--project', str(SIM_PROJ), '--','--config', str(cfg_path), '--run-id', run_id]
    out = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
    if out.returncode != 0:
        print('SIM FAIL', run_id, out.returncode, file=sys.stderr)
        print(out.stdout, file=sys.stderr)
        print(out.stderr, file=sys.stderr)
        sys.exit(out.returncode)
    run_dir = ROOT / 'journals' / 'M0' / f'M0-RUN-{run_id}'
    if not run_dir.exists():
        print(f'Run dir missing: {run_dir}', file=sys.stderr)
        sys.exit(2)
    return run_dir

def norm_trades(path: pathlib.Path):
    txt = path.read_text(encoding='utf-8').splitlines()
    if not txt:
        return b''
    header = txt[0].split(',')
    try:
        drop_idx = header.index('config_hash')
    except ValueError:
        drop_idx = None
    out_lines = []
    for line in txt[1:]:
        if not line.strip():
            continue
        parts = line.split(',')
        if drop_idx is not None and drop_idx < len(parts):
            parts = [p for i,p in enumerate(parts) if i != drop_idx]
        out_lines.append(','.join(parts))
    return '\n'.join(out_lines).encode('utf-8')

def norm_events(path: pathlib.Path):
    lines = path.read_text(encoding='utf-8').splitlines()
    if len(lines) < 2:
        return b''
    # skip meta + header
    body = lines[2:]
    return '\n'.join(body).encode('utf-8')

def sha256(b: bytes):
    return hashlib.sha256(b).hexdigest().upper()

def first_line(path: pathlib.Path, token: str):
    for l in path.read_text(encoding='utf-8').splitlines():
        if token in l:
            return l
    return ''

def main():
    variants = {
        'off': mutate('off'),
        'shadow': mutate('shadow'),
        'active_no_breach': mutate('active', no_breach=True),
        'active_with_breach': mutate('active', breach=True)
    }
    results = {}
    for name, cfg in variants.items():
        cfg_file = ART / f'acc.{name}.json'
        dump(cfg_file, cfg)
        run_dir = run_sim(cfg_file, f'ACC-{name.upper()}')
        events_csv = run_dir / 'events.csv'
        trades_csv = run_dir / 'trades.csv'
        ev_sha = sha256(norm_events(events_csv))
        tr_sha = sha256(norm_trades(trades_csv))
        res = {
            'run_dir': str(run_dir.relative_to(ROOT)),
            'events_sha': ev_sha,
            'trades_sha': tr_sha,
            'eval_line': first_line(events_csv, 'INFO_RISK_EVAL_V1'),
            'alert_block_exposure': first_line(events_csv, 'ALERT_BLOCK_NET_EXPOSURE'),
            'alert_block_drawdown': first_line(events_csv, 'ALERT_BLOCK_DRAWDOWN')
        }
        results[name] = res
    print(json.dumps(results, indent=2))

if __name__ == '__main__':
    main()
