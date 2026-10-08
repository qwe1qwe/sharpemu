// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

// A wait-on-address record stalls only its own queue. The submitting thread must get
// control back, because it may be the one that later lets the waited-for value appear.
[Collection("AmprFileRegistry")]
public sealed class AmprDeferredSubmissionTests
{
    private const string WaitOnAddressNid = "V7GQTEeUfhw";
    private const string WriteAddressNid = "j0+3uJMxYJY";
    private const ulong MemoryBase = 0x3_0000_0000;
    private const ulong Counter = MemoryBase + 0x800;
    private const ulong Done = MemoryBase + 0x808;
    private const ulong LaterDone = MemoryBase + 0x810;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void WaitingSubmissionReturnsAndFinishesOnceAnotherQueueWritesTheValue()
    {
        var (context, manager) = Create();
        var waiting = CommandBuffer(context, 0x000);
        WaitEquals(context, manager, waiting, Counter, 2);
        Write(context, manager, waiting, Done, 1);

        Assert.Equal(0, AmprExports.SubmitCommandBuffer(context, waiting, 1, null, out var pending, out _, out _));
        Assert.NotNull(pending);
        Assert.Equal(0UL, Read(context, Done));

        var producer = CommandBuffer(context, 0x400);
        Write(context, manager, producer, Counter, 2);
        Assert.Equal(0, AmprExports.SubmitCommandBuffer(
            context, producer, AmprExports.AmmQueueKeyBase, null, out var producerPending, out var result, out _));
        Assert.Null(producerPending);
        Assert.Equal(0, result);

        Assert.True(pending!.Wait(Timeout));
        Assert.Equal(0, pending.ExecutionResult);
        Assert.Equal(1UL, Read(context, Done));
    }

    [Fact]
    public void LaterSubmissionsOnTheSameQueueStayBehindTheWait()
    {
        var (context, manager) = Create();
        var waiting = CommandBuffer(context, 0x000);
        WaitEquals(context, manager, waiting, Counter, 5);
        Write(context, manager, waiting, Done, 1);
        var completedOnWorker = 0;
        Assert.Equal(0, AmprExports.SubmitCommandBuffer(
            context, waiting, 7, (_, _) => Interlocked.Increment(ref completedOnWorker), out var first, out _, out _));

        var later = CommandBuffer(context, 0x400);
        Write(context, manager, later, LaterDone, 9);
        Assert.Equal(0, AmprExports.SubmitCommandBuffer(context, later, 7, null, out var second, out _, out _));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(0UL, Read(context, LaterDone));

        Assert.True(context.TryWriteUInt64(Counter, 5));
        Assert.True(second!.Wait(Timeout));
        Assert.True(first!.IsCompleted);
        Assert.Equal(1, completedOnWorker);
        Assert.Equal(1UL, Read(context, Done));
        Assert.Equal(9UL, Read(context, LaterDone));
    }

    private static (CpuContext Context, ModuleManager Manager) Create()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return (new CpuContext(memory, Generation.Gen5), manager);
    }

    // Command buffer object at +offset, its 0x100-byte record buffer behind it.
    private static ulong CommandBuffer(CpuContext context, ulong offset)
    {
        var commandBuffer = MemoryBase + offset;
        context[CpuRegister.Rdi] = commandBuffer;
        context[CpuRegister.Rsi] = commandBuffer + 0x100;
        context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));
        return commandBuffer;
    }

    private static void WaitEquals(CpuContext context, ModuleManager manager, ulong commandBuffer, ulong address, ulong value)
    {
        context[CpuRegister.Rdi] = commandBuffer;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = value;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(WaitOnAddressNid, context));
    }

    private static void Write(CpuContext context, ModuleManager manager, ulong commandBuffer, ulong address, ulong value)
    {
        context[CpuRegister.Rdi] = commandBuffer;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = value;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(WriteAddressNid, context));
    }

    private static ulong Read(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt64(address, out var value));
        return value;
    }
}
