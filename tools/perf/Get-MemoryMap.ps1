#Requires -Version 7
<#
.SYNOPSIS
  A small VMMap-style summary of a process's committed memory: what the private bytes are made of.

.DESCRIPTION
  Read-only. Walks the target's address space with VirtualQueryEx and groups committed memory by kind:
    * GC heap     - private regions inside reservations of 256 MB or more (the .NET GC reserves huge ranges)
    * Code (JIT)  - private executable regions
    * Stacks      - private regions with a guard page in the same reservation
    * Native heap - other private read/write regions
    * Image       - mapped DLL/EXE files (shared unless written), with the top modules by committed size
    * Mapped      - other mapped files and shared sections
  Private bytes in Task Manager roughly equal the private kinds plus written (copy-on-write) image pages.

.EXAMPLE
  pwsh -NoProfile -File tools/perf/Get-MemoryMap.ps1 -ProcessId 10052
#>
param([Parameter(Mandatory)][int]$ProcessId, [int]$TopModules = 12)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MemMap {
    [StructLayout(LayoutKind.Sequential)]
    public struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId;
        public IntPtr RegionSize; public uint State, Protect, Type; }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, IntPtr len);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetMappedFileNameW(IntPtr h, IntPtr addr, System.Text.StringBuilder name, uint size);
}
'@

$PROCESS_QUERY_INFORMATION = 0x0400; $PROCESS_VM_READ = 0x0010
$h = [MemMap]::OpenProcess($PROCESS_QUERY_INFORMATION -bor $PROCESS_VM_READ, $false, $ProcessId)
if ($h -eq [IntPtr]::Zero) { throw "OpenProcess failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$MEM_COMMIT = 0x1000; $MEM_IMAGE = 0x1000000; $MEM_MAPPED = 0x40000; $MEM_PRIVATE = 0x20000; $PAGE_GUARD = 0x100
$exec = 0x10, 0x20, 0x40, 0x80
$regions = [Collections.Generic.List[object]]::new()
$addr = [IntPtr]::Zero; $mbi = New-Object MemMap+MBI; $size = [IntPtr][Runtime.InteropServices.Marshal]::SizeOf($mbi)
try {
    while ([MemMap]::VirtualQueryEx($h, $addr, [ref]$mbi, $size) -ne [IntPtr]::Zero) {
        $regions.Add([pscustomobject]@{ Base = $mbi.BaseAddress.ToInt64(); AllocBase = $mbi.AllocationBase.ToInt64()
            Size = $mbi.RegionSize.ToInt64(); State = $mbi.State; Protect = $mbi.Protect; Type = $mbi.Type })
        $next = $mbi.BaseAddress.ToInt64() + $mbi.RegionSize.ToInt64()
        if ($next -le $addr.ToInt64()) { break }
        $addr = [IntPtr]$next
    }
    # Reservation sizes and guard pages per allocation base.
    $byAlloc = $regions | Where-Object { $_.AllocBase -ne 0 } | Group-Object AllocBase
    $resSize = @{}; $hasGuard = @{}
    foreach ($g in $byAlloc) {
        $resSize[[long]$g.Name] = ($g.Group | Measure-Object Size -Sum).Sum
        $hasGuard[[long]$g.Name] = [bool]($g.Group | Where-Object { ($_.Protect -band $PAGE_GUARD) -ne 0 })
    }
    $kinds = [ordered]@{ 'GC heap' = 0L; 'Code (JIT)' = 0L; 'Stacks' = 0L; 'Native heap / other private' = 0L; 'Image' = 0L; 'Mapped' = 0L }
    $modules = @{}
    foreach ($r in $regions | Where-Object { $_.State -eq $MEM_COMMIT }) {
        if ($r.Type -eq $MEM_PRIVATE) {
            if ($resSize[$r.AllocBase] -ge 256MB) { $kinds['GC heap'] += $r.Size }
            elseif ($exec -contains ($r.Protect -band 0xFF)) { $kinds['Code (JIT)'] += $r.Size }
            elseif ($hasGuard[$r.AllocBase]) { $kinds['Stacks'] += $r.Size }
            else { $kinds['Native heap / other private'] += $r.Size }
        } elseif ($r.Type -eq $MEM_IMAGE) {
            $kinds['Image'] += $r.Size
            $sb = [Text.StringBuilder]::new(520)
            $null = [MemMap]::GetMappedFileNameW($h, [IntPtr]$r.Base, $sb, 520)
            $name = if ($sb.Length) { [IO.Path]::GetFileName($sb.ToString()) } else { '?' }
            $modules[$name] = ($modules[$name] ?? 0L) + $r.Size
        } else { $kinds['Mapped'] += $r.Size }
    }
} finally { $null = [MemMap]::CloseHandle($h) }

$p = Get-Process -Id $ProcessId
'{0} (pid {1}): working set {2:N0} MB, private bytes {3:N0} MB' -f $p.ProcessName, $ProcessId, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB)
''
'Committed memory by kind (MB):'
$kinds.GetEnumerator() | ForEach-Object { '  {0,-30} {1,8:N1}' -f $_.Key, ($_.Value / 1MB) }
''
"Top $TopModules images by committed size (MB; shared with other processes unless written):"
$modules.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $TopModules |
    ForEach-Object { '  {0,-45} {1,8:N1}' -f $_.Key, ($_.Value / 1MB) }
