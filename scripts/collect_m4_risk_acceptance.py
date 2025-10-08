import json, subprocess, pathlib, hashlib, sys, os, re, argparse, time

def find_root(start: pathlib.Path) -> pathlib.Path:
    cur = start
    markers = [
        ("src", "TiYf.Engine.Sim"),
        ("TiYf.Engine.sln",),
        (".git",)
    ]
    for _ in range(10):  # bounded ascent
        for m in markers:
            p = cur.joinpath(*m)
            if p.exists():
                return cur
        if cur.parent == cur:
            break
        cur = cur.parent
    raise RuntimeError("Could not locate repo root. Run with --root <path>.")

def parse_args():
    ap = argparse.ArgumentParser()
    ap.add_argument('--root', default=None, help='Repo root override (optional).')
    ap.add_argument('--print-debug', action='store_true', help='Print debug diagnostics to stderr.')
    return ap.parse_args()

def get_root():
    args = parse_args()
    if args.root:
        return pathlib.Path(args.root).resolve()
    return find_root(pathlib.Path(__file__).resolve().parent)

ARGS = parse_args()
ROOT = ARGS.root and pathlib.Path(ARGS.root).resolve() or get_root()
SIM_PROJ = ROOT / 'src' / 'TiYf.Engine.Sim'
ART = ROOT / 'acceptance_tmp'
BASE = ROOT / 'tests' / 'fixtures' / 'backtest_m0' / 'config.backtest-m0.json'
ART.mkdir(exist_ok=True)

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

def snapshot_run_dirs():
    base = ROOT / 'journals' / 'M0'
    if not base.exists():
        return {}
    result = {}
    for d in base.iterdir():
        if d.is_dir() and d.name.startswith('M0-RUN'):
            try:
                result[d.name] = d.stat().st_mtime_ns
            except FileNotFoundError:
                continue
    return result

def detect_new_dir(before: dict, after: dict):
    new_names = [n for n in after.keys() if n not in before]
    if new_names:
        # pick newest among new
        new_names.sort(key=lambda n: after[n], reverse=True)
        return new_names[0], new_names
    # fallback: changed mtime larger than max(before)
    if not before:
        if after:
            best = max(after.items(), key=lambda kv: kv[1])[0]
            return best, [best]
        return None, []
    max_before = max(before.values())
    candidates = [n for n,v in after.items() if v > max_before]
    if not candidates:
        return None, []
    candidates.sort(key=lambda n: after[n], reverse=True)
    return candidates[0], candidates

def run_sim(cfg_path: pathlib.Path, run_id: str):
    cmd = ['dotnet','run','-c','Release','--project', str(SIM_PROJ), '--','--config', str(cfg_path), '--run-id', run_id]
    before = snapshot_run_dirs()
    out = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
    if out.returncode != 0:
        print('SIM FAIL', run_id, out.returncode, file=sys.stderr)
        print(out.stdout, file=sys.stderr)
        print(out.stderr, file=sys.stderr)
        sys.exit(out.returncode)
    # slight delay to allow FS timestamp flush
    time.sleep(0.05)
    after = snapshot_run_dirs()
    chosen, candidates = detect_new_dir(before, after)
    if not chosen:
        print(f'RUN DIR DETECTION FAILURE for {run_id}', file=sys.stderr)
        print('Before:', before, file=sys.stderr)
        print('After :', after, file=sys.stderr)
        sys.exit(3)
    run_dir = ROOT / 'journals' / 'M0' / chosen
    if ARGS.print_debug:
        print(f'[collector] run_id={run_id} chosen_dir={chosen} candidates={candidates}', file=sys.stderr)
    events_csv = run_dir / 'events.csv'
    trades_csv = run_dir / 'trades.csv'
    if not events_csv.exists() or not trades_csv.exists():
        print(f'MISSING JOURNAL FILES for {run_id} dir={run_dir} events={events_csv.exists()} trades={trades_csv.exists()}', file=sys.stderr)
        sys.exit(4)
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
        # Try parity artifact reuse
        parity_dir = ROOT / 'artifacts' / 'parity'
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
