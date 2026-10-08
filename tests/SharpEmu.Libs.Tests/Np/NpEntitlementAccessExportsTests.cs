// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpEntitlementAccessExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Theory]
    [InlineData("GHOST2APP0000000")]
    [InlineData("GHOST2BASE000000")]
    public void GhostOfYoteiMainEntitlement_IsReportedAsInstalled(string label)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1_000);
        var context = new CpuContext(memory, Generation.Gen5);
        var labelAddress = MemoryBase + 0x100;
        var infoAddress = MemoryBase + 0x200;
        memory.WriteCString(labelAddress, label);
        context[CpuRegister.Rsi] = labelAddress;
        context[CpuRegister.Rdx] = infoAddress;

        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessGetAddcontEntitlementInfo(context));

        Span<byte> info = stackalloc byte[0x1C];
        Assert.True(memory.TryRead(infoAddress, info));
        Assert.Equal(label, ReadCString(info[..17]));
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(info[20..]));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(info[24..]));
    }

    [Fact]
    public void GhostOfYoteiOptionalEntitlement_RemainsUnowned()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1_000);
        var context = new CpuContext(memory, Generation.Gen5);
        var labelAddress = MemoryBase + 0x100;
        var infoAddress = MemoryBase + 0x200;
        memory.WriteCString(labelAddress, "GHOST2DDE0000000");
        context[CpuRegister.Rsi] = labelAddress;
        context[CpuRegister.Rdx] = infoAddress;

        Assert.Equal(
            unchecked((int)0x817D0007),
            NpEntitlementAccessExports.NpEntitlementAccessGetAddcontEntitlementInfo(context));
    }

    [Fact]
    public void UnifiedEntitlementQuery_CompletesOfflineWithNoEntriesAndNoFurtherPage()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1_000);
        var context = new CpuContext(memory, Generation.Gen5);
        var requestIdAddress = MemoryBase + 0x100;
        context[CpuRegister.Rdi] = 0x1000_0000;
        context[CpuRegister.R8] = MemoryBase + 0x200;
        context[CpuRegister.R9] = requestIdAddress;
        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessRequestUnifiedEntitlementInfoList(context));
        Assert.True(context.TryReadUInt64(requestIdAddress, out var requestId));
        Assert.NotEqual(0UL, requestId);

        // Wolverine polls with result, a 100-entry list, hit count, next offset (in its
        // request parameter) and previous offset on the stack.
        var resultAddress = MemoryBase + 0x300;
        var hitNumAddress = MemoryBase + 0x304;
        var nextOffsetAddress = MemoryBase + 0x308;
        var previousOffsetAddress = MemoryBase + 0x30C;
        memory.TryWrite(resultAddress, new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 7, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0 });
        context[CpuRegister.Rdi] = requestId;
        context[CpuRegister.Rsi] = resultAddress;
        context[CpuRegister.Rdx] = MemoryBase + 0x400;
        context[CpuRegister.Rcx] = 100;
        context[CpuRegister.R8] = hitNumAddress;
        context[CpuRegister.R9] = nextOffsetAddress;
        context.SetImportStackArguments(previousOffsetAddress, 0, 0, 0, 0, 0);
        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessPollUnifiedEntitlementInfoList(context));

        Span<byte> output = stackalloc byte[16];
        Assert.True(memory.TryRead(resultAddress, output));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output[4..]));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(output[8..]));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(output[12..]));

        context[CpuRegister.Rdi] = requestId;
        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessDeleteRequest(context));
        context[CpuRegister.Rdi] = requestId;
        Assert.Equal(
            unchecked((int)0x817D0002),
            NpEntitlementAccessExports.NpEntitlementAccessPollUnifiedEntitlementInfoList(context));
    }

    private static string ReadCString(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(value[..(length < 0 ? value.Length : length)]);
    }
}
