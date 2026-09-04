# H# v7 vs Go vs C: threaded networking benchmark

Tests H#'s new `Http.ListenRaw` + `OnAccept` pattern (real thread-per-connection,
introduced in H# v0.7.0) against a comparably "optimized" Go server
(goroutine-per-connection) and C server (single-threaded epoll, edge-triggered,
`writev`). All three respond with the same 2-byte "ok" body so this is a fair,
matched comparison, unlike some earlier payload-mismatched rounds.

## Test environment (exact specs)

- **CPU**: Intel(R) Xeon(R) Processor @ 2.10GHz, **1 core visible** (`nproc` = 1)
- **RAM**: 3.9 GiB total
- **Kernel**: Linux 6.18.44, x86_64
- **OS**: Ubuntu 24.04.4 LTS
- **GCC**: 13.3.0
- **Go**: go 1.22.2 linux/amd64
- **.NET SDK**: 8.0.130 (used to build H#'s compiler)
- **clang**: 18.1.3 (used by H#'s compiler for linking)
- **Load generator**: ApacheBench (ab) 2.3

**This is a single-core sandboxed container, not dedicated multi-core
hardware.** That is the single most important caveat for everything below;
see the Caveats section.

## Files

- `hsharp_v7_listenraw_threaded.hs`: H# server using `Http.ListenRaw(19320)`
  with `.OnAccept((RawHttpPacket raw) => {...})`, which spawns a real OS
  thread per connection (new in v0.7.0; earlier versions had only a single
  blocking accept loop).
- `go_optimized_2byte.go`: Go server, goroutine-per-connection via
  `go func(c net.Conn) {...}(conn)`, `TCP_NODELAY` set, `GOMAXPROCS` set
  to `runtime.NumCPU()`. Listens on port 19399.
- `c_optimized_2byte.c`: C server, single-threaded edge-triggered epoll,
  `writev` for a combined head+body send, `-O3 -march=native`. Listens on
  port 19298. (This is the same file used in earlier 64KB-payload rounds;
  reused here since its logic already matches this payload size for a
  2-byte body.)

All three: accept, one read of the incoming request, one write of a fixed
`HTTP/1.0 200 OK ... ok` response, close.

## Build

```bash
# H# (requires .NET 8 SDK, LLVM 18, clang, lld; see H#'s own README)
dotnet run --project <path-to-HSharp-compiler> -- hsharp_v7_listenraw_threaded.hs -o hs_listenraw_threaded

# Go
go build -o go_optimized_2byte go_optimized_2byte.go

# C
gcc -O3 -march=native -o c_optimized_2byte c_optimized_2byte.c
```

## Run + benchmark

```bash
# start a server, then in another shell:
ab -n 3000 -c 1 -q http://127.0.0.1:<port>/
ab -n 5000 -c 50 -q http://127.0.0.1:<port>/
ab -n 8000 -c 200 -q http://127.0.0.1:<port>/
ab -n 10000 -c 500 -q http://127.0.0.1:<port>/
```

### Ports

| File | Port |
|---|---|
| hsharp_v7_listenraw_threaded.hs | 19320 |
| go_optimized_2byte.go | 19399 |
| c_optimized_2byte.c | 19298 |

## Results (this sandbox, 1 core; see caveats)

| Concurrency | H# v7 ListenRaw (thread-per-conn) | Go optimized (goroutine-per-conn) | C optimized (epoll, single-thread) |
|---|---|---|---|
| c=1 | 24,660 req/sec | 19,259 req/sec | 19,426 req/sec |
| c=50 | 26,451 req/sec | 22,721 req/sec | 20,809 req/sec |
| c=200 | 26,785 req/sec | 22,050 req/sec | 19,129 req/sec |
| c=500 | 25,217 req/sec | 19,468 req/sec | 16,650 req/sec |

H# led at every concurrency level tested here, and unlike the single-threaded
version tested before v0.7.0, it did not degrade as concurrency rose from
1 to 500 connections.

## Caveats (read before drawing conclusions)

- **Single core.** This is the big one. Thread-per-connection designs
  (what H# now uses) are well known to degrade at very high concurrency
  (thousands of connections) due to OS thread-creation and context-switch
  overhead, an effect that mainly shows up with real multi-core hardware
  and much higher connection counts than were tested here (500 max). C's
  single-threaded epoll design exists specifically to scale past that
  ceiling without per-connection thread overhead, and is widely used in
  production precisely for that property. This test did not find any
  ceiling for H#'s threading model, but a single core and 500 connections
  is not enough to rule one out. Rerun on real multi-core hardware with
  concurrency in the thousands before trusting this generalizes.
- **No averaging across multiple trials.** Each row is a single `ab` run.
  Expect real run-to-run noise; rerun each test 3-5 times and average
  before treating any specific number as precise.
- **Go's goroutine model is specifically built for high-concurrency
  scenarios**, so it underperforming its own reputation here is very
  plausibly a single-core artifact (goroutines can't actually spread
  across cores that don't exist), not evidence Go is weak at this in
  general.
- **Small (2-byte) payload only.** This isolates connection-handling
  overhead, not data-transfer throughput. Earlier testing (not included in
  this specific file set) found that at large payloads (64KB, 128MiB) all
  three languages converge to within noise of each other, since transfer
  bandwidth dominates once payloads are large enough. Small-payload,
  high-connection-churn results like these should not be read as "H# is
  faster than C/Go" in general, only in this specific regime.
- Not tested: real production HTTP workloads (routing, header parsing
  depth, keep-alive/persistent connections, TLS), which would exercise
  parts of each server this micro-benchmark does not touch.
