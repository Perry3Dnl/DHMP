# DHMP Gen-2 native processor lab

This directory contains the experimental Linux/C processor-path lab used to screen DHMP receive strategies before porting promising ideas back to the .NET prototype.

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
