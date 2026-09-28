"""Run PZSEDUMP and print the entire accoreconsole report, unfiltered."""
from aps_run import run

log = run("PZSEDUMP\n", want_output=False, show=tuple())
print("")
print("=" * 66)
print("FULL REPORT")
print("=" * 66)
print(log if log else "(empty)")
