# Native cost index fixes

Branch `perf/native-fixes-cost`

All tests in the table are in `CodexBarWin.Tests.Unit/Services/NativeCostScannerTests.cs`. Scanner source paths are relative to `Services/Native/Cost/`.

| Change | Behavior | Source files | Regression tests |
| --- | --- | --- | --- |
| 1. Time-zone changes and index recovery | Schema v2 stores UTC seconds and the zone ID. A zone change reassigns usage days and rebuilds the aggregates in a single transaction using indexed data. Older or invalid indexes are rebuilt on update. | `CostIndex.cs`, `CostIndexDatabase.cs`, `NativeCostScanner.cs` | `Update_WhenZoneChanges_RebucketsStoredUsageWithoutReadingLogs`, `Update_WhenIndexIsInvalid_RebuildsFromLogs` (old version, missing trigger, missing timestamp column, corrupt file) |
| 2. Read-only reports | Reports open SQLite with `SQLITE_OPEN_READONLY` and read committed data from a snapshot. A missing index returns an empty report without creating the index directory. | `CostIndexDatabase.cs`, `NativeCostScanner.cs` | `ReadReport_WhenIndexIsMissing_ReturnsEmptyWithoutCreatingDirectory`, `ReadReport_WhenWriterHasUncommittedChanges_ReturnsCommittedRows` |
| 3. Reset deduplication | A reset deletes offset-keyed usage from the preceding generation at that path. A delete trigger subtracts those rows from the day buckets. Provider-keyed usage and usage from paths that disappear are still retained. | `CostIndex.cs`, `CostIndexDatabase.cs`, `NativeCostScanner.cs` | `Update_WhenUnkeyedFileIsReset_ReplacesOffsetUsageAndKeepsKeyedUsage` (shrink and same-size identity replacement) |
| 4. Unreadable logs | Enumeration ignores inaccessible entries. A file that cannot be read counts toward `SkippedFiles` and retains its cursor. If it fails after writing SQLite rows, the batch rolls back and replays its readable files. | `NativeCostScanner.cs`, `CostIndexDatabase.cs` | `Update_WhenChangedFileIsLocked_ScansOtherFilesAndRetriesFromPreviousOffset` |
| 5. Resumable first scan | Usage, daily totals and file cursors commit together at file boundaries after 100 files or about 256 MiB of pending input. Cancellation preserves committed batches. A per-index mutex serializes updates across batch boundaries. | `NativeCostScanner.cs` | `Update_WhenColdScanIsCancelled_ResumesFromCommittedBatch` |
| 6. Windows background priority | The caller and parallel scan workers enter Windows background mode through two `LibraryImport` methods. The scope restores priority when it exits, including on cancellation. | `NativeCostScanner.cs`, `Directory.Build.props` | `Update_WhenCancelledAfterCommit_RestoresThreadPriority` |

`Directory.Build.props` enables unsafe code for the three projects that compile the generated interop stubs. It also gives the Windows SDK the .NET MSBuild host's architecture when `PROCESSOR_ARCHITECTURE` is absent, which fixes MSIX recipe generation with the requested Release build command.

## Verification

| Command | Result |
| --- | --- |
| `dotnet build CodexBarWin.csproj -c Release -p:Platform=x64 -p:PublishReadyToRun=false` | Passed. 0 errors, 1 warning (`NETSDK1198`, missing `win-x64.pubxml`). |
| `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64` | 2,115 passed, 0 failed, 0 skipped, 2,115 total. |
| `dotnet build tools/CostProbe/CostProbe.csproj -c Release` | Passed. 0 errors, 0 warnings. |
| `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64 --no-build --filter FullyQualifiedName~NativeCostScannerTests` | 20 passed, 0 failed, 0 skipped, 20 total. The 12 added cases passed. |

The scanner tests exercised Windows SQLite, file sharing, cancellation and thread priority with synthetic logs. Reset detection still relies on observed shrinkage, creation-time changes or file identity changes, so an in-place rewrite that regrows before discovery can escape detection.
