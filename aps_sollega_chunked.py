"""Generate the Sollega / San Diego Intl Airport sheet set in chunks.

The account ignores limitProcessingTimeSec (the stored activity reads back
None), so a run is cut off near 100s regardless of what the activity asks for.
Activating a layout to make its viewports render costs several seconds on this
104k-entity drawing, so 30 sheets cannot finish in one workitem.

Each chunk chains its output object into the next chunk's input, so the partly
built drawing never leaves the bucket. The plan is deterministic â€” sheet N is
the same sheet in every chunk â€” which is what lets the pieces compose.

PZSESLIM runs only in the final chunk: it prunes the template tab, and every
earlier chunk still needs that tab to clone from.

  python aps_sollega_chunked.py            # chunk size 4, from the beginning
  python aps_sollega_chunked.py 3          # chunk size 3
  python aps_sollega_chunked.py 4 --resume 12 3   # resume at sheet 12, step 3
"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run
from aps_sollega import PARAMS

SRC = Path(__file__).parent / "_scratch" / "sollega_sdia_rev7_run.dwg"

# 1 key plan + 29 array sheets, from the PZSESHEETS plan.
TOTAL = 30

if __name__ == "__main__":
    argv = sys.argv[1:]
    chunk = int(argv[0]) if argv and argv[0].isdigit() else 4

    start, step = 0, 0
    if "--resume" in argv:
        i = argv.index("--resume")
        start, step = int(argv[i + 1]), int(argv[i + 2])

    tok = get_token()[1]["access_token"]

    if "--skip-upload" not in argv and step == 0:
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, "sollega_chain_00.dwg", SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    while start < TOTAL:
        n = min(chunk, TOTAL - start)
        last = start + n >= TOTAL
        aps_run.IN_OBJ = f"sollega_chain_{step:02d}.dwg"
        aps_run.OUT_OBJ = f"sollega_chain_{step+1:02d}.dwg"
        params = dict(PARAMS, startAt=start, count=n)
        # PZSEKEYLAB must run BEFORE PZSESLIM: it calls PlanSheets, which sizes
        # the plan window from the template tab, and SLIM prunes that tab.
        script = ("PZSEMAKE\nPZSEKEYLAB\nPZSESLIM\nPZSETITLE\nPZSEFAST\nPZSESAVE\n"
                  if last else "PZSEMAKE\nPZSESAVE\n")
        print(f"\n### chunk {step+1}: sheets [{start}, {start+n})"
              f"{'  (final, +SLIM)' if last else ''}"
              f"  {aps_run.IN_OBJ} -> {aps_run.OUT_OBJ}")
        log = aps_run.run(script, params=params, want_output=True,
                          show=("MAKE-DONE", "FAIL", "chunk:", "SLIM"))
        if not log or "MAKE-DONE" not in log:
            raise SystemExit(
                f"chunk {step+1} did not complete.\n"
                f"  resume with: python aps_sollega_chunked.py {chunk} "
                f"--resume {start} {step} --skip-upload")
        start += n
        step += 1

    final = f"sollega_chain_{step:02d}.dwg"
    print(f"\nfinal object: {final}")

    # A run's deliverable is the DWG *and* the monochrome PDF and PNGs. Plotting
    # used to be a manual afterthought, so a set could be handed over having
    # never actually been plotted.
    if "--no-publish" not in argv:
        import aps_publish
        aps_publish.publish(final, "sollega_sdia")
