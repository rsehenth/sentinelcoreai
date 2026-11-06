"""Exercise runner failure paths against a disposable, fully synthetic toolchain."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent
SAFE = 'SentinelCore.Tests.FakeTests.Safe'
REAL = 'SentinelCore.Tests.FakeTests.Integration'
MOCK = r'''
$mode = $env:T4B_PROBE_MODE
$root = Split-Path $PSScriptRoot -Parent
$marker = Join-Path $root 'calls.txt'
if ($args[0] -eq 'build') {
    Add-Content $marker 'build'
    if ($mode -eq 'build-fail') { exit 1 }
    exit 0
}
if ('--list-tests' -in $args) {
    Add-Content $marker 'discover'
    if ('--filter' -in $args) { '    SentinelCore.Tests.FakeTests.Safe'; exit 0 }
    if ($mode -ne 'removed') { '    SentinelCore.Tests.FakeTests.Safe' }
    if ($mode -eq 'renamed') { '    SentinelCore.Tests.FakeTests.Renamed' }
    '    SentinelCore.Tests.FakeTests.Integration'
    if ($mode -eq 'extra' -or $mode -eq 'theory') { '    SentinelCore.Tests.FakeTests.Unapproved(status: 1)' }
    exit 0
}
Add-Content $marker 'execute-fake'
if ($mode -eq 'interrupt') { exit 130 }
$dir = $args[[array]::IndexOf($args, '--results-directory') + 1]
$case = if ($mode -eq 'bad-trx') { 'SentinelCore.Tests.FakeTests.Wrong' } else { 'SentinelCore.Tests.FakeTests.Safe' }
@"
<TestRun><Results><UnitTestResult testName="$case" /></Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
"@ | Set-Content -Encoding UTF8 (Join-Path $dir 'synthetic.trx')
exit 0
'''


def run_probe(name, scenario='ok', mutate=None, must_execute=False, expect_pass=False, args=()):
    artifacts = ROOT / 'artifacts'
    artifacts.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='runner-probe-', dir=artifacts) as temp:
        root = Path(temp)
        for directory in ['scripts', 'tests/SentinelCore.Tests', 'src']:
            (root / directory).mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / 'tests/run-synthetic.ps1', root / 'tests/run-synthetic.ps1')
        (root / 'scripts/dotnet.ps1').write_text(MOCK, encoding='utf-8')
        (root / 'tests/SentinelCore.Tests/SentinelCore.Tests.csproj').write_text('<Project/>', encoding='utf-8')
        (root / 'src/Probe.cs').write_text('// synthetic dependency', encoding='utf-8')
        (root / 'Directory.Build.props').write_text('<Project/>', encoding='utf-8')
        files = [p for p in root.rglob('*') if p.is_file()]
        manifest = {
            'synthetic': [SAFE], 'windowsIntegration': [REAL], 'windowsOperational': [], 'regressionReady': True,
            'discoveredCases': [SAFE, REAL],
            'inputs': [{'path': p.relative_to(root).as_posix(),
                        'sha256': hashlib.sha256(p.read_bytes()).hexdigest().upper()} for p in files],
        }
        manifest_path = root / 'tests/synthetic-audit.json'
        manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
        if mutate:
            mutate(root, manifest, manifest_path)
        env = dict(os.environ, T4B_PROBE_MODE=scenario)
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(root / 'tests/run-synthetic.ps1'), *args],
                                cwd=root, env=env, capture_output=True, text=True, timeout=20)
        marker = root / 'calls.txt'
        calls = marker.read_text().splitlines() if marker.exists() else []
        if (result.returncode == 0) != expect_pass or ('execute-fake' in calls) != must_execute:
            raise AssertionError(f'{name}: exit={result.returncode}, calls={calls}, stderr={result.stderr}')
        print(f'PASS {name}: calls={calls}', flush=True)


def alter_file(path, content):
    return lambda root, manifest, manifest_path: (root / path).write_text(content, encoding='utf-8')


def update_manifest(change):
    def apply(root, manifest, manifest_path):
        change(manifest)
        manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
    return apply


if __name__ == '__main__':
    run_probe('baseline', must_execute=True, expect_pass=True)
    run_probe('selecao-aprovada', must_execute=True, expect_pass=True, args=['-Cases', SAFE])
    run_probe('selecao-nao-aprovada', args=['-Cases', REAL])
    run_probe('regressao-bloqueada', mutate=update_manifest(lambda m: m.update(regressionReady=False)))
    run_probe('hash-divergente', mutate=alter_file('src/Probe.cs', '// changed'))
    run_probe('manifesto-invalido', mutate=lambda r, m, p: p.write_text('{', encoding='utf-8'))
    run_probe('arquivo-novo', mutate=alter_file('tests/SentinelCore.Tests/New.cs', '// new'))
    run_probe('dependencia-alterada', mutate=alter_file('tests/SentinelCore.Tests/SentinelCore.Tests.csproj', '<Project><Changed/></Project>'))
    run_probe('configuracao-nova', mutate=alter_file('Directory.Build.targets', '<Project/>'))
    run_probe('selecionado-nao-aprovado', mutate=update_manifest(lambda m: m['synthetic'].append('SentinelCore.Tests.FakeTests.Unknown')))
    run_probe('integracao-na-selecao', mutate=update_manifest(lambda m: m['synthetic'].append(REAL)))
    run_probe('operacional-na-selecao', mutate=update_manifest(lambda m: m['windowsOperational'].append(SAFE)))
    run_probe('metodo-removido', scenario='removed')
    run_probe('metodo-renomeado', scenario='renamed')
    run_probe('descoberta-divergente', scenario='extra')
    run_probe('theory-nova', scenario='theory')
    run_probe('falha-build', scenario='build-fail')
    run_probe('trx-inconsistente', scenario='bad-trx', must_execute=True)
    run_probe('interrupcao', scenario='interrupt', must_execute=True)
