// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ShaderClockTests
{
    // SMEM s_memtime / s_memrealtime s[4:5]: opcode in bits 25:18, SDATA in bits 12:6.
    [Theory]
    [InlineData(0x24u, "SMemtime")]
    [InlineData(0x25u, "SMemrealtime")]
    public void DecodesTheClockIntoAScalarPairWithoutMemoryAccess(uint opcode, string name)
    {
        var word = 0xF400_0000u | (opcode << 18) | (4u << 6);
        var context = new CpuContext(new InstructionMemory([word, 0, 0xBF810000]), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        Assert.Equal(2, program.Instructions.Count);
        var clock = program.Instructions[0];
        Assert.Equal(name, clock.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Smem, clock.Encoding);
        Assert.Empty(clock.Sources);
        Assert.Equal(new[] { Gen5Operand.Scalar(4), Gen5Operand.Scalar(5) }, clock.Destinations);
        Assert.Null(clock.Control);
        Assert.Equal(8u, program.Instructions[1].Pc);

        var memory = MemoryAccessTable.Build(program);
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void ClockReadsCompileToSpirv()
    {
        var clock = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Smem, "SMemrealtime", [0xF4940280, 0], [],
            [Gen5Operand.Scalar(10), Gen5Operand.Scalar(11)], null);
        var program = Program(
            clock,
            MoveVectorFromScalar(8, 2, 10),
            BufferAccess(12, "BufferStoreDword", 4, vectorData: 2),
            EndProgram(20));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("shaderClock", Encoding.ASCII.GetString(shader.Spirv));
    }

    private sealed class InstructionMemory(uint[] words) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || (address - 0x1000) % sizeof(uint) != 0 || destination.Length != sizeof(uint))
                return false;
            var index = (address - 0x1000) / sizeof(uint);
            if (index >= (ulong)words.Length) return false;
            BinaryPrimitives.WriteUInt32LittleEndian(destination, words[(int)index]);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
