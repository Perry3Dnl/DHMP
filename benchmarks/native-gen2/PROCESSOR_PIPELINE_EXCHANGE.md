# Post-stream processor pipeline A/B

This lab isolates the new DHMP processor split:

```text
Stream Processor -> Package Exchange -> Model Processor -> Application
```

It deliberately excludes sockets so TCP scheduling does not hide the cost of the post-stream handoff.

All modes generate the same deterministic 32-byte package, materialize the same 32-byte model and compute the same checksum.

Modes:

- `direct`: package extraction and model materialization stay on one thread. This is the no-exchange floor.
- `spsc`: Stream Processor publishes each completed package to a bounded SPSC exchange; Model Processor consumes one package at a time on another thread.
- `batch`: same bounded exchange and two processors, but the Model Processor drains up to N packages per acquisition loop.

The queue is bounded and preallocated. This benchmark does not claim the queue implementation is final DHMP architecture.

Build/run:

```bash
gcc -O3 -march=native -pthread processor_pipeline_exchange_ab.c -o pipeline-ab
for i in 1 2 3 4 5 6 7; do
  ./pipeline-ab direct 50000000
  ./pipeline-ab spsc 50000000
  ./pipeline-ab batch 50000000 384
done
```

Record wall throughput, producer CPU/message, model CPU/message, aggregate CPU/message and checksum. The checksum MUST match between modes before performance results are retained.

Important: `direct` uses one core while exchange modes intentionally use two concurrent processors. Compare both wall throughput and aggregate CPU; do not describe a wall-throughput win as a CPU-efficiency win.
