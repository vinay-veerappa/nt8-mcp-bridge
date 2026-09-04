"""One-shot: wire the new gate and battery into CI. Throwaway."""
import io

p = ".github/workflows/ci.yml"
s = io.open(p, encoding="utf-8").read()

gate_old = ("      - name: One order-liveness predicate, not four\n"
            "        run: python tools/check_single_order_liveness.py\n")
gate_new = gate_old + (
    "\n"
    "      # The Strategy Analyzer window is REUSED across calls, and the strategy was\n"
    "      # applied with the lenient SetP, so an unresolvable name failed silently and the\n"
    "      # window kept whatever it already had. Measured 2026-09-04: a request for\n"
    "      # '@SampleMACrossOver' ran '_McpTestBot' and returned totalTrades: 0 --\n"
    "      # indistinguishable from the requested strategy simply not having traded.\n"
    "      # McpBridgeAddOn.cs is the one bridge source tests/BridgeTests.csproj cannot\n"
    "      # compile, so this gate is the ONLY automated reader of that contract.\n"
    "      - name: Backtest runs the strategy it was asked for\n"
    "        run: python tools/check_backtest_strategy_echo.py\n")
assert s.count(gate_old) == 1, ("gate anchor", s.count(gate_old))
s = s.replace(gate_old, gate_new, 1)

bat_old = ("      - name: Mutation battery P1-106\n"
           "        run: python mutation/mutate_p1106.py\n")
assert s.count(bat_old) == 1, ("battery anchor", s.count(bat_old))
bat_new = bat_old + (
    "\n"
    "      # Killer is a SOURCE GATE, not the C# suite: McpBridgeAddOn.cs cannot be built\n"
    "      # by tests/BridgeTests.csproj, so no unit test can reach Backtest(). A gate is\n"
    "      # worth exactly what its mutants prove.\n"
    "      - name: Mutation battery - backtest strategy echo\n"
    "        run: python mutation/mutate_backtest_strategy_echo.py\n")
s = s.replace(bat_old, bat_new, 1)

io.open(p, "w", encoding="utf-8", newline="\n").write(s)
print("wired gate + battery into CI")
