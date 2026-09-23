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


## Ring-3 cache-line layout A/B

`ring3_cacheline_ab.c` compares the current packed 96-byte Ring-3 state block with a version that puts each 32-byte state on its own 64-byte cache line.

Build:

```bash
gcc -O3 -march=native -pthread ring3_cacheline_ab.c -o ring3-cache-ab
./ring3-cache-ab packed
./ring3-cache-ab isolated
```

The retained result favors the compact layout: median producer CPU cost was 8.215 ns per complete three-state update for packed 96 B versus 14.662 ns for 64-byte-isolated slots. See [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) for details.


## Ring-2 vs Ring-3 with a 10 µs held state

`ring2_vs_ring3_hold10us.c` compares a safe two-slot swap design with the three-slot design while the consumer holds a zero-copy state for 10 µs.

Both variants use the same atomic slot ownership state machine and validate every consumed 32-byte state for torn reads.

Build:

```bash
gcc -O3 -march=native -pthread ring2_vs_ring3_hold10us.c -o ring23-hold
./ring23-hold 2
./ring23-hold 3
```

The retained seven-run medians were effectively tied on producer throughput (~30.64 M publications/s), while Ring-3 delivered ~1.8% more useful consumer publications. All retained runs had zero validation errors.

Raw data: [../results/ring2-vs-ring3-hold10us-2026-09-23.csv](../results/ring2-vs-ring3-hold10us-2026-09-23.csv)


## Ring-3 single-atomic triple exchange

`ring3_triple_exchange_ab.c` compares the safe per-slot ownership state machine against a classic SPSC `FRONT / MIDDLE / BACK` triple-buffer exchange.

The optimized path keeps producer and consumer ownership local and uses one shared atomic middle-token containing the slot index plus a dirty bit.

Build:

```bash
gcc -O3 -march=native -pthread ring3_triple_exchange_ab.c -o ring3-exchange-ab
./ring3-exchange-ab baseline
./ring3-exchange-ab exchange
```

Seven alternating retained runs with a 10 µs zero-copy consumer hold produced a median **32.359 M producer publications/s** for the exchange path versus **30.281 M/s** for the per-slot baseline. Producer CPU cost fell from 33.023 ns to 30.900 ns/publication, and all retained runs reported zero validation errors.

Raw results: [../results/ring3-triple-exchange-ab-2026-09-23.csv](../results/ring3-triple-exchange-ab-2026-09-23.csv)


## Ring-3 CPU microbenchmarks

The current processor sweep also includes:

- `ring3_token_width_ab.c` — 8/16/32/64-bit publication-token widths;
- `ring3_contract_specialize_ab.c` — runtime-size processor versus a negotiated 32-byte specialized path;
- `ring3_carry32_ab.c` — variable-size carry `memmove` versus fixed 32-byte vector carry handling.

Retained conclusions:

- 32-bit token remains the default;
- negotiated specialization provides a small but measurable CPU win;
- fixed-width 32-byte carry handling removes most of the isolated variable-size carry-copy cost.

See [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) for raw-result links and interpretation.


## Fixed output-slab processor batching

`processor_output_slab_ab.c` compares per-result Ring-3 ownership transfers with a processor that writes 384 fixed 32-byte results into one 12 KiB output slab and publishes the completed slab once.

The retained seven-run medians were **2.251 ns/result** for per-result publication versus **0.887 ns/result** for the output-slab path. The consumer validates every frame of acquired slabs; all retained runs completed with zero validation errors.

Raw results: [../results/processor-output-slab-ab-2026-09-23.csv](../results/processor-output-slab-ab-2026-09-23.csv)


## Consumer CAS vs exchange

`ring3_consumer_cas_vs_exchange.c` tests replacing the consumer's compare-and-swap acquisition with a single atomic exchange after observing the dirty bit.

The exchange version is ownership-correct and eliminates retries, but the retained x86-64 benchmark did not show a speed win. CAS remains the preferred default because the real receive-batch path already has very few CAS failures and the unconditional exchange can increase cache-line ownership traffic.

Raw results: [../results/ring3-consumer-cas-vs-exchange-2026-09-23.csv](../results/ring3-consumer-cas-vs-exchange-2026-09-23.csv)
