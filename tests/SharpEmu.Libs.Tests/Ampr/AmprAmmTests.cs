// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AmprAmmTests
{
    [Fact]
    public void GetVirtualAddressRangesReportsReservedAmmAndMultimapWindows()
    {
        using var test = new BackedKernelMemory();
        var ranges = test.Output + 0x200;
        // Stack residue the title does not clear before the call.
        for (ulong i = 0; i < 4; i++)
            Assert.True(test.Context.TryWriteUInt64(ranges + i * 8, 0xC0DEC0DECAFEBA00));

        SetRangePointers(test, ranges);
        Assert.Equal(0, AmprExports.AmmGetVirtualAddressRanges(test.Context));

        var (start, end, multimapStart, multimapEnd) = ReadRanges(test, ranges);
        Assert.NotEqual(0UL, start);
        Assert.Equal(KernelMemoryCompatExports.AmmRangeBytes, end - start);
        Assert.Equal(end, multimapStart);
        Assert.Equal(KernelMemoryCompatExports.AmmMultimapRangeBytes, multimapEnd - multimapStart);
        Assert.Equal(0UL, start % 0x200000);
        Assert.Equal((start, multimapEnd), test.Query(start));
    }

    [Fact]
    public void GetVirtualAddressRangesReturnsTheSameWindowOnRepeatedCalls()
    {
        using var test = new BackedKernelMemory();
        SetRangePointers(test, test.Output + 0x200);
        Assert.Equal(0, AmprExports.AmmGetVirtualAddressRanges(test.Context));
        var first = ReadRanges(test, test.Output + 0x200);

        SetRangePointers(test, test.Output + 0x300);
        Assert.Equal(0, AmprExports.AmmGetVirtualAddressRanges(test.Context));
        Assert.Equal(first, ReadRanges(test, test.Output + 0x300));
    }

    [Fact]
    public void GetVirtualAddressRangesRejectsNullOutput()
    {
        using var test = new BackedKernelMemory();
        SetRangePointers(test, 0);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AmprExports.AmmGetVirtualAddressRanges(test.Context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GiveDirectMemoryAllocatesAlignedDirectMemory(int usage)
    {
        using var test = new BackedKernelMemory();
        const ulong length = 0x600000;
        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = GuestMemoryLayout.DirectBytes;
        test.Context[CpuRegister.Rdx] = length;
        test.Context[CpuRegister.Rcx] = 0x200000;
        test.Context[CpuRegister.R8] = (ulong)usage;
        test.Context[CpuRegister.R9] = test.Output;

        Assert.Equal(0, AmprExports.AmmGiveDirectMemory(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output, out var offset));
        Assert.Equal(0UL, offset % 0x200000);
        Assert.Equal(usage == 1 ? length : 0UL, KernelMemoryCompatExports.AmmPoolBytes);

        // The handed-out range is owned by AMM now; a kernel allocation must not overlap it.
        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = GuestMemoryLayout.DirectBytes;
        test.Context[CpuRegister.Rdx] = 0x4000;
        test.Context[CpuRegister.Rcx] = 0x4000;
        test.Context[CpuRegister.R8] = 0;
        test.Context[CpuRegister.R9] = test.Output + 8;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output + 8, out var other));
        Assert.True(other + 0x4000 <= offset || other >= offset + length);
    }

    [Fact]
    public void GiveDirectMemoryRejectsUnknownUsage()
    {
        using var test = new BackedKernelMemory();
        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = GuestMemoryLayout.DirectBytes;
        test.Context[CpuRegister.Rdx] = 0x200000;
        test.Context[CpuRegister.Rcx] = 0x200000;
        test.Context[CpuRegister.R8] = 2;
        test.Context[CpuRegister.R9] = test.Output;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AmprExports.AmmGiveDirectMemory(test.Context));
    }

    [Fact]
    public void SubmittedMapMakesTheRangeUsableAndUnmapReturnsThePages()
    {
        using var test = new BackedKernelMemory();
        var (start, _, _, _) = ReserveWindow(test);
        var poolOffset = Give(test, 0x100000, usage: 1);
        var (commandBuffer, buffer) = CreateCommandBuffer(test);

        Assert.Equal(0, RecordMap(test, commandBuffer, start, 0x8000, protection: 0x33));
        test.Context[CpuRegister.Rdi] = commandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferGetBufferBaseAddress(test.Context));
        Assert.Equal(buffer, test.Context[CpuRegister.Rax]);
        Assert.Equal(0, AmprExports.CommandBufferGetCurrentOffset(test.Context));
        var size = test.Context[CpuRegister.Rax];
        Assert.InRange(size, 1UL, 0x40UL);

        Assert.Equal(0, Submit(test, buffer, size));
        Assert.True(test.Context.TryWriteUInt64(start + 0x7FF8, 0x1122334455667788));
        Assert.True(test.Context.TryReadUInt64(start + 0x7FF8, out var value));
        Assert.Equal(0x1122334455667788UL, value);
        Assert.Equal(0x100000UL - 0x8000, KernelMemoryCompatExports.AmmPoolFreeBytes);

        test.Context[CpuRegister.Rdi] = commandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferReset(test.Context));
        test.Context[CpuRegister.Rdi] = commandBuffer;
        test.Context[CpuRegister.Rsi] = start;
        test.Context[CpuRegister.Rdx] = 0x8000;
        Assert.Equal(0, AmprExports.AmmCommandBufferUnmap(test.Context));
        Assert.Equal(0, Submit(test, buffer, 0x20));
        Assert.Equal(0x100000UL, KernelMemoryCompatExports.AmmPoolFreeBytes);

        // The range stays reserved and can be mapped again.
        test.Context[CpuRegister.Rdi] = commandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferReset(test.Context));
        Assert.Equal(0, RecordMap(test, commandBuffer, start, 0x4000, protection: 0x33));
        Assert.Equal(0, Submit(test, buffer, 0x20));
        Assert.True(test.Context.TryWriteUInt64(start, 1));
        Assert.True(poolOffset % 0x200000 == 0);
    }

    [Fact]
    public void MapStitchesPagesFromAFragmentedPool()
    {
        using var test = new BackedKernelMemory();
        var (start, _, _, _) = ReserveWindow(test);
        _ = Give(test, 0x8000, usage: 1, alignment: 0x4000);
        // A kernel allocation between the two gifts keeps their pool runs apart.
        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = GuestMemoryLayout.DirectBytes;
        test.Context[CpuRegister.Rdx] = 0x4000;
        test.Context[CpuRegister.Rcx] = 0x4000;
        test.Context[CpuRegister.R8] = 0;
        test.Context[CpuRegister.R9] = test.Output + 0x288;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(test.Context));
        _ = Give(test, 0x8000, usage: 1, alignment: 0x4000);
        var (commandBuffer, buffer) = CreateCommandBuffer(test);

        Assert.Equal(0, RecordMap(test, commandBuffer, start, 0xC000, protection: 0x33));
        Assert.Equal(0, Submit(test, buffer, 0x20));
        for (ulong page = 0; page < 0xC000; page += 0x4000)
            Assert.True(test.Context.TryWriteUInt64(start + page, page + 1));
        Assert.Equal(0x4000UL, KernelMemoryCompatExports.AmmPoolFreeBytes);
    }

    [Fact]
    public void MapWithoutPoolPagesReportsTheFailureThroughSubmit()
    {
        using var test = new BackedKernelMemory();
        var (start, _, _, _) = ReserveWindow(test);
        var (commandBuffer, buffer) = CreateCommandBuffer(test);

        Assert.Equal(0, RecordMap(test, commandBuffer, start, 0x4000, protection: 0x33));
        Assert.Equal(0, Submit(test, buffer, 0x20));
        Assert.False(test.Context.TryWriteUInt64(start, 1));
    }

    [Fact]
    public void SubmitRejectsBadPriorityAndNullBuffer()
    {
        using var test = new BackedKernelMemory();
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Submit(test, 0x1000, 0x20, priority: 3));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, Submit(test, 0, 0x20));
    }

    private static void SetRangePointers(BackedKernelMemory test, ulong ranges)
    {
        test.Context[CpuRegister.Rdi] = ranges;
        test.Context[CpuRegister.Rsi] = ranges == 0 ? 0 : ranges + 8;
        test.Context[CpuRegister.Rdx] = ranges == 0 ? 0 : ranges + 16;
        test.Context[CpuRegister.Rcx] = ranges == 0 ? 0 : ranges + 24;
    }

    private static (ulong Start, ulong End, ulong MultimapStart, ulong MultimapEnd) ReserveWindow(BackedKernelMemory test)
    {
        SetRangePointers(test, test.Output + 0x200);
        Assert.Equal(0, AmprExports.AmmGetVirtualAddressRanges(test.Context));
        return ReadRanges(test, test.Output + 0x200);
    }

    private static ulong Give(BackedKernelMemory test, ulong length, int usage, ulong alignment = 0x200000)
    {
        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = GuestMemoryLayout.DirectBytes;
        test.Context[CpuRegister.Rdx] = length;
        test.Context[CpuRegister.Rcx] = alignment;
        test.Context[CpuRegister.R8] = (ulong)usage;
        test.Context[CpuRegister.R9] = test.Output + 0x280;
        Assert.Equal(0, AmprExports.AmmGiveDirectMemory(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x280, out var offset));
        return offset;
    }

    private static (ulong CommandBuffer, ulong Buffer) CreateCommandBuffer(BackedKernelMemory test)
    {
        var commandBuffer = test.Output + 0x1000;
        var buffer = test.Output + 0x2000;
        test.Context[CpuRegister.Rdi] = commandBuffer;
        test.Context[CpuRegister.Rsi] = buffer;
        test.Context[CpuRegister.Rdx] = 0x1000;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(test.Context));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(test.Context));
        return (commandBuffer, buffer);
    }

    private static int RecordMap(BackedKernelMemory test, ulong commandBuffer, ulong address, ulong size, int protection)
    {
        test.Context[CpuRegister.Rdi] = commandBuffer;
        test.Context[CpuRegister.Rsi] = address;
        test.Context[CpuRegister.Rdx] = size;
        test.Context[CpuRegister.Rcx] = 0xC;
        test.Context[CpuRegister.R8] = (ulong)protection;
        return AmprExports.AmmCommandBufferMap(test.Context);
    }

    private static int Submit(BackedKernelMemory test, ulong buffer, ulong size, int priority = 1)
    {
        test.Context[CpuRegister.Rdi] = buffer;
        test.Context[CpuRegister.Rsi] = size;
        test.Context[CpuRegister.Rdx] = (ulong)priority;
        return AmprExports.AmmSubmitCommandBuffer(test.Context);
    }

    private static (ulong Start, ulong End, ulong MultimapStart, ulong MultimapEnd) ReadRanges(
        BackedKernelMemory test, ulong address)
    {
        Assert.True(test.Context.TryReadUInt64(address, out var start));
        Assert.True(test.Context.TryReadUInt64(address + 8, out var end));
        Assert.True(test.Context.TryReadUInt64(address + 16, out var multimapStart));
        Assert.True(test.Context.TryReadUInt64(address + 24, out var multimapEnd));
        return (start, end, multimapStart, multimapEnd);
    }
}
