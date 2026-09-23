# DHMP Gen-2 native processor lab

This directory contains the experimental Linux/C processor-path lab used to screen DHMP receive strategies before porting promising ideas back to the .NET prototype.

The directory also contains a directly inspectable Ring-3 reference benchmark: `ring3_fixed_slab_latest.c`.

The source archive contains:
- `gen2_processor_lab.c`
- `Makefile`
- `run_two_pass_suite.py`
- the lab README used during the search

The lab is not the DHMP production implementation. Its absolute Linux/native throughput numbers must not be compared directly with the Windows/.NET benchmark numbers.

Current test assumptions:
- fixed frame contract and no per-frame DHMP header;
- slab target `clamp(frameSize * 512, 16 KiB, 1 MiB)`;
- fixed reusable memory;
- `Every` processes every complete frame;
- `Latest` may conflate stale complete frames while retaining a stable newest frame;
- retained Gen-2 screening points use two runs per point.

See [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) for the consolidated results and caveats.

## Ring-3 Fixed-Slab Latest

This experimental path keeps exactly three permanent retained frame slots and a separate fixed reusable receive slab.

For a 32-byte contract the retained state payload is 96 bytes. The reference source intentionally avoids per-frame allocation, queue growth, state-slot shifting, and clearing. Older complete frames that cannot survive the bounded Latest window are skipped by fixed-size arithmetic.

Build and run on Linux:

```bash
gcc -O3 -march=native -pthread ring3_fixed_slab_latest.c -o ring3bench
./ring3bench 32 2.0 12288
```

Arguments are `frame_bytes`, `seconds`, and `slab_bytes`.

The retained five-pass 32-byte reference results are stored in [../results/ring3-fixed-slab-latest-32b-2026-09-23.csv](../results/ring3-fixed-slab-latest-32b-2026-09-23.csv). See [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) for interpretation and caveats.
