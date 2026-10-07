// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// Kernel-side state for the libSceAmpr AMM (address memory manager). AMM titles
// ask once for the virtual windows AMM maps into and hand a slice of direct
// memory to AMM up front; the windows live in the regular mapping table as one
// reservation so later maps can land inside them.
public static partial class KernelMemoryCompatExports
{
    internal const ulong AmmRangeBytes = 32UL * 1024 * 1024 * 1024;
    internal const ulong AmmMultimapRangeBytes = 32UL * 1024 * 1024 * 1024;
    internal const int AmmUsageDirectOnly = 0;
    internal const int AmmUsagePool = 1;
    private const ulong AmmWindowAlignment = 0x200000;

    private static readonly object _ammGate = new();
    // Guarded by _memoryGate so a mapping reset clears it together with the reservation.
    private static ulong _ammWindowBase;
    private static ulong _ammPoolBytes;
    // Free physical runs of the AMM page pool (offset -> length), filled by usage-1 gifts.
    private static readonly SortedDictionary<ulong, ulong> _ammPoolFree = new();
    // Plain AMM maps currently in place (virtual address -> run), each backed by one pool run.
    private static readonly SortedDictionary<ulong, AmmMapping> _ammMappings = new();

    private readonly record struct AmmMapping(ulong Length, ulong Physical);

    internal readonly record struct AmmVirtualAddressRanges(
        ulong Start,
        ulong End,
        ulong MultimapStart,
        ulong MultimapEnd);

    // Reserves (once) and reports the AMM window followed by the multimap window.
    // Both are carved from a single reservation so it is large enough to take the
    // sparse path: Windows cannot always place a contiguous 32 GiB host placeholder.
    // scratchPointer is a guest qword the reservation helper reads its hint from and
    // writes the chosen address to.
    internal static int GetAmmVirtualAddressRanges(
        CpuContext ctx,
        ulong scratchPointer,
        out AmmVirtualAddressRanges ranges)
    {
        ranges = default;
        lock (_ammGate)
        {
            ulong windowBase;
            lock (_memoryGate)
            {
                windowBase = _ammWindowBase;
            }

            if (windowBase == 0)
            {
                if (!ctx.TryWriteUInt64(scratchPointer, 0))
                    return MemoryFault;

                var result = ReserveBackingRange(
                    ctx,
                    scratchPointer,
                    AmmRangeBytes + AmmMultimapRangeBytes,
                    flags: 0,
                    AmmWindowAlignment);
                if (result != 0)
                    return result;
                if (!ctx.TryReadUInt64(scratchPointer, out windowBase) || windowBase == 0)
                    return MemoryFault;

                lock (_memoryGate)
                {
                    _ammWindowBase = windowBase;
                }

                Console.Error.WriteLine(
                    $"[LOADER][INFO] ampr.amm window reserved: amm=0x{windowBase:X16}..0x{windowBase + AmmRangeBytes:X16} " +
                    $"multimap=0x{windowBase + AmmRangeBytes:X16}..0x{windowBase + AmmRangeBytes + AmmMultimapRangeBytes:X16}");
            }

            var multimapStart = windowBase + AmmRangeBytes;
            ranges = new AmmVirtualAddressRanges(
                windowBase,
                multimapStart,
                multimapStart,
                multimapStart + AmmMultimapRangeBytes);
            return 0;
        }
    }

    // Allocates direct memory on behalf of AMM. Usage 1 puts it into the pool that
    // AMM's plain maps draw from; usage 0 only allocates it for direct maps.
    internal static int GiveAmmDirectMemory(
        long searchStartRaw,
        long searchEndRaw,
        ulong length,
        ulong alignment,
        int usage,
        out ulong offset)
    {
        offset = 0;
        if (length == 0 || usage is not (AmmUsageDirectOnly or AmmUsagePool))
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;

        var limit = GuestMemoryLayout.DirectBytes;
        var searchEnd = searchEndRaw <= 0 ? limit : Math.Min((ulong)searchEndRaw, limit);
        var searchStart = searchStartRaw < 0 ? 0UL : (ulong)searchStartRaw;
        if (searchStart >= searchEnd)
            searchStart = 0;

        var align = alignment == 0 ? OrbisPageSize : alignment;
        lock (_memoryGate)
        {
            if (!TryAllocateDirectMemoryLocked(searchStart, searchEnd, length, align, memoryType: 0, limit, out offset))
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
            if (usage == AmmUsagePool)
            {
                _ammPoolBytes += length;
                ReturnAmmPagesLocked(offset, length);
            }
        }

        return 0;
    }

    internal static ulong AmmPoolFreeBytes
    {
        get
        {
            lock (_memoryGate)
            {
                var total = 0UL;
                foreach (var run in _ammPoolFree.Values)
                    total += run;
                return total;
            }
        }
    }

    // Executes an AMM plain map: takes pages from the pool and maps them at the
    // fixed address inside the AMM window. scratchPointer is a guest qword holding
    // the target address (the command record's address field); the kernel map
    // helper reads its request from it and writes the result back.
    internal static int AmmMap(CpuContext ctx, ulong address, ulong length, int protection, ulong scratchPointer)
    {
        if (length == 0 || !IsAligned(address, OrbisPageSize) || !IsAligned(length, OrbisPageSize) ||
            address > ulong.MaxValue - length || !TryDecodeMappedProtection(protection, out _) ||
            (protection & OrbisProtCpuExec) != 0)
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;

        lock (_ammGate)
        {
            List<(ulong Address, ulong Length, ulong Physical)> runs;
            lock (_memoryGate)
            {
                if (OverlapsAmmMappingLocked(address, length))
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS;
                if (!TryTakeAmmPagesLocked(address, length, out runs))
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
            }

            var mapped = 0;
            for (; mapped < runs.Count; mapped++)
            {
                var run = runs[mapped];
                var result = ctx.TryWriteUInt64(scratchPointer, run.Address)
                    ? MapDirectMemoryCore(ctx, scratchPointer, run.Length, protection, OrbisKernelMapFixed, run.Physical, 0)
                    : MemoryFault;
                if (result != 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] ampr.amm_map failed va=0x{run.Address:X} size=0x{run.Length:X} phys=0x{run.Physical:X} prot=0x{protection:X} result=0x{result:X8}");
                    for (var undo = 0; undo < mapped; undo++)
                        _ = UnmapAmmRunToReservation(ctx, runs[undo].Address, runs[undo].Length);
                    lock (_memoryGate)
                    {
                        foreach (var taken in runs)
                            ReturnAmmPagesLocked(taken.Physical, taken.Length);
                    }
                    return result;
                }
            }

            // Leave the caller's record holding the address it asked for.
            _ = ctx.TryWriteUInt64(scratchPointer, address);
            lock (_memoryGate)
            {
                foreach (var run in runs)
                    _ammMappings[run.Address] = new AmmMapping(run.Length, run.Physical);
            }
            return 0;
        }
    }

    // Executes an AMM unmap: the pages go back to the pool and the range stays
    // reserved, so a later map can land there again. Ranges without an AMM map are ignored.
    internal static int AmmUnmap(CpuContext ctx, ulong address, ulong length)
    {
        if (length == 0 || !IsAligned(address, OrbisPageSize) || !IsAligned(length, OrbisPageSize) ||
            address > ulong.MaxValue - length)
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;

        lock (_ammGate)
        {
            List<(ulong Address, ulong Length, ulong Physical)> pieces = [];
            lock (_memoryGate)
            {
                var end = address + length;
                foreach (var (start, mapping) in _ammMappings)
                {
                    if (start >= end)
                        break;
                    var mappingEnd = start + mapping.Length;
                    if (mappingEnd <= address)
                        continue;
                    var cutStart = Math.Max(start, address);
                    var cutEnd = Math.Min(mappingEnd, end);
                    pieces.Add((cutStart, cutEnd - cutStart, mapping.Physical + (cutStart - start)));
                }
            }

            foreach (var piece in pieces)
            {
                var result = UnmapAmmRunToReservation(ctx, piece.Address, piece.Length);
                if (result != 0)
                    return result;
                lock (_memoryGate)
                {
                    RemoveAmmMappingRangeLocked(piece.Address, piece.Length);
                    ReturnAmmPagesLocked(piece.Physical, piece.Length);
                }
            }
            return 0;
        }
    }

    private static int UnmapAmmRunToReservation(CpuContext ctx, ulong address, ulong length)
        => RunMappingTransaction(() =>
        {
            lock (_memoryGate)
            {
                var space = ResolveBackingSpace(ctx);
                var regions = GetMappingSlices(address, length);
                if (space is null || !TryUnmapViews(space, regions))
                    return MemoryAccessDenied;
                ReplaceMappedRegionRangeLocked(new MappedRegion(address, length, 0, false, false, 0, IsReserved: true));
                return 0;
            }
        });

    private static bool OverlapsAmmMappingLocked(ulong address, ulong length)
    {
        var end = address + length;
        foreach (var (start, mapping) in _ammMappings)
        {
            if (start >= end)
                return false;
            if (start + mapping.Length > address)
                return true;
        }
        return false;
    }

    // Prefers one physically contiguous run; falls back to stitching the map
    // together from several free runs when the pool is fragmented.
    private static bool TryTakeAmmPagesLocked(
        ulong address,
        ulong length,
        out List<(ulong Address, ulong Length, ulong Physical)> runs)
    {
        runs = [];
        foreach (var (offset, runLength) in _ammPoolFree)
        {
            if (runLength < length)
                continue;
            TakeAmmPoolRangeLocked(offset, runLength, length);
            runs.Add((address, length, offset));
            return true;
        }

        var free = 0UL;
        foreach (var runLength in _ammPoolFree.Values)
            free += runLength;
        if (free < length)
            return false;

        var cursor = address;
        var remaining = length;
        while (remaining != 0)
        {
            var (offset, runLength) = _ammPoolFree.First();
            var take = Math.Min(runLength, remaining);
            TakeAmmPoolRangeLocked(offset, runLength, take);
            runs.Add((cursor, take, offset));
            cursor += take;
            remaining -= take;
        }
        return true;
    }

    private static void TakeAmmPoolRangeLocked(ulong offset, ulong runLength, ulong take)
    {
        _ammPoolFree.Remove(offset);
        if (take < runLength)
            _ammPoolFree[offset + take] = runLength - take;
    }

    private static void ReturnAmmPagesLocked(ulong offset, ulong length)
    {
        var start = offset;
        var end = offset + length;
        ulong? previous = null;
        foreach (var key in _ammPoolFree.Keys)
        {
            if (key >= start)
                break;
            previous = key;
        }
        if (previous is { } before && before + _ammPoolFree[before] == start)
        {
            start = before;
            _ammPoolFree.Remove(before);
        }
        if (_ammPoolFree.TryGetValue(end, out var after))
        {
            _ammPoolFree.Remove(end);
            end += after;
        }
        _ammPoolFree[start] = end - start;
    }

    private static void RemoveAmmMappingRangeLocked(ulong address, ulong length)
    {
        var end = address + length;
        var affected = _ammMappings.Where(pair => pair.Key < end && pair.Key + pair.Value.Length > address).ToArray();
        foreach (var (start, mapping) in affected)
        {
            _ammMappings.Remove(start);
            var mappingEnd = start + mapping.Length;
            if (start < address)
                _ammMappings[start] = new AmmMapping(address - start, mapping.Physical);
            if (mappingEnd > end)
                _ammMappings[end] = new AmmMapping(mappingEnd - end, mapping.Physical + (end - start));
        }
    }

    internal static ulong AmmPoolBytes
    {
        get
        {
            lock (_memoryGate)
            {
                return _ammPoolBytes;
            }
        }
    }

    private static void ResetAmmStateLocked()
    {
        _ammWindowBase = 0;
        _ammPoolBytes = 0;
        _ammPoolFree.Clear();
        _ammMappings.Clear();
    }
}
