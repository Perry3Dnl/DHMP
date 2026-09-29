# Direct-IP measurement rules

1. Freeze the implementation and benchmark contract before comparison.
2. Use the same host, CPU allocation, payload, packet batching and consumer work.
3. Validate byte identity and publication semantics before timing.
4. Report allocations, warm-up, repeated rotated runs and distributions, not only a best run.
5. Separate logical offered messages from received, validated and consumed messages.
6. Record malformed packets, loss, duplicates and ordering/freshness behavior.
7. Label memory-only, kernel-loopback, NIC/link and physical two-host results separately.
8. State whether every record/byte is inspected or a whole batch is only counted.
9. Do not merge different benchmark generations into one performance ranking.
10. Preserve V1 mock contracts; use a new A/B harness for header-template optimizations.

Architecture independence is a project decision. Better physical network performance must
still be measured. There is no active compatibility path merely for maintaining old comparisons.
