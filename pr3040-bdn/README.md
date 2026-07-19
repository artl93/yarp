# PR 3040 cross-platform BenchmarkDotNet evidence

This task-scoped harness compares exact `Yarp.ReverseProxy.dll` binaries from:

- baseline `49b23da6d8b0e9df47abf45d7755173f84670941`
- candidate `4e8c0103f27f6ff322785353a777da0902b8bbef`

The binaries are stored in local NuGet packages so each BenchmarkDotNet job gets an isolated dependency graph. `GlobalSetup` hashes the assembly actually loaded by each benchmark process and fails unless it matches:

- baseline: `0c813f18eeea95564753baa3032c9a8a2dc1b92caa25f2985bdba313400bd9f4`
- candidate: `d1e6a57fa93b0aa829680b05f46a859da3b58404a15c19813f59448ecb7ac190`

The workflow runs the same hash-verified dry validation and apples-to-apples ShortRun on Linux x64, Windows x64, and macOS arm64. The asynchronous stub returns a genuinely pending `ValueTask` and is completed inline after the proxy pipeline returns; this exercises the async middleware path without scheduler noise.

