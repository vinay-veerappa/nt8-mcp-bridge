"""Parse-check the bridge sources NO TEST BUILD CAN SEE.

WHAT THIS IS FOR. `tests/BridgeTests.csproj` compiles the harness and
`addons/BridgeAccountResolver.cs` -- and NOT `addons/McpBridgeAddOn.cs`, which is
6000 lines naming NinjaTrader's whole type surface (`P2-27`, `tests/README.md`).
So a stray brace, an unterminated string or a missing semicolon in the bridge
passes every gate this repo has and is first reported by NinjaTrader's own
compiler. At that point a compile error in ANY addon `.cs` stops EVERY addon
loading -- **the risk guard included**. That is not a hypothetical: it is the
documented reason `P1-72`..`P1-75` could only be compile-checked by deploying.

This is the pre-flight that lets an edit to the bridge be checked BEFORE it is
written to a live NT8 folder.

WHAT THIS IS NOT. It is a PARSER check, not a compile. It reports only CS1xxx
diagnostics -- the ones the lexer and parser raise -- and deliberately ignores
CS0xxx, which are binder errors about NinjaTrader types this project does not
reference (`AddOnBase`, `Account`, `Instrument`, ...). So it answers "is this file
syntactically valid C#?" and NOT "does it type-check?".

  A PASS HERE IS NOT A COMPILE. `nt_compile` inside NT8 is still required before
  any bridge change is called done, and nothing here substitutes for it.

Ported from nt8-riskguard's `tools/check_window_parses.py`, which solves the same
problem for `TradeCopierWindow.cs`. Bounding what a check proves is the point --
that file records why a full compile was attempted first and abandoned (it pulled
in `NinjaTrader.Custom.dll`, which already contains a compiled copy of these same
sources, so every type resolved twice and the errors were about the harness).
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))

# Every bridge-owned addon source. The resolver is here as well as in the test
# build on purpose: the test build targets net8.0 and NT8 compiles net48, and this
# check is the cheaper of the two places to notice a syntax regression.
TARGETS = ['McpBridgeAddOn.cs', 'BridgeAccountResolver.cs']

PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Library</OutputType>
    <EnableDefaultItems>false</EnableDefaultItems>
    <LangVersion>latest</LangVersion>
    <NoWarn>$(NoWarn);CS0169;CS0414;CS0649</NoWarn>
  </PropertyGroup>
  <ItemGroup>
%s  </ItemGroup>
</Project>
"""


def main():
    if shutil.which('dotnet') is None:
        print('CANNOT RUN: no dotnet SDK on PATH. This check is being SKIPPED, not passed.')
        return 2

    work = tempfile.mkdtemp(prefix='nt8bridgeparse_')
    try:
        includes = ''
        for t in TARGETS:
            src = os.path.join(REPO, 'addons', t)
            if not os.path.exists(src):
                print('CANNOT RUN: %s is missing. SKIPPED, not passed.' % t)
                return 2
            includes += '    <Compile Include="%s" />\n' % src
        proj = os.path.join(work, 'ParseCheck.csproj')
        with open(proj, 'w', encoding='utf-8') as f:
            f.write(PROJECT % includes)

        r = subprocess.run(['dotnet', 'build', proj, '-v', 'q', '--nologo'],
                           capture_output=True, text=True)
        # CS1xxx == lexer/parser. CS0xxx == binder, i.e. the NinjaTrader types we
        # deliberately did not reference, which is expected and not a finding.
        syntax = sorted(set(
            line.strip() for line in (r.stdout + r.stderr).splitlines()
            if re.search(r'error CS1\d{3}:', line)))

        for line in syntax:
            print('  [SYNTAX] %s' % line)

        if syntax:
            print('\n%d syntax error(s) in code no test build compiles. NT8 would refuse '
                  'the WHOLE Custom assembly for these, which stops every addon loading '
                  '-- the risk guard included.' % len(syntax))
            return 1

        print('OK: %s parse(s) as valid C#.' % ', '.join(TARGETS))
        print('    This is NOT a compile -- type errors are out of scope by design. '
              'Run nt_compile before calling a bridge change done.')
        return 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
