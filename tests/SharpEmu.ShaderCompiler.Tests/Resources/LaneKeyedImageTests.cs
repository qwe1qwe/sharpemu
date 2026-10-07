// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Image descriptors selected per lane through a waterfall loop: the key is loaded from
// a buffer, made uniform with v_readfirstlane and shifted into a heap V# offset.
public sealed class LaneKeyedImageTests
{
    private const uint KeyMask = 0x000F_FFFF;
    private const uint IdentitySwizzle = 0xFAC;
    private const uint Format32x2Uint = 62;

    // s[0:3] key buffer, s[4:7] descriptor heap, s30 a lane condition.
    private static Gen5ShaderProgram WaterfallProgram(string keyOperation = "VAndB32", bool formatted = false, int loops = 1)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0x1000;
        void Add(Func<uint, Gen5ShaderInstruction> make) { instructions.Add(make(pc)); pc += 8; }

        uint keyRegister;
        if (formatted)
        {
            // v[2:3] = record.xy; v5 = condition ? y : 0.
            Add(current => BufferAccess(current, "BufferLoadFormatXy", 0, dwords: 2, vectorData: 2, indexEnabled: true));
            Add(current => Vop3(current, "VCndmaskB32", 5, Operand(0), Gen5Operand.Vector(3), Gen5Operand.Scalar(30)));
            keyRegister = 5;
        }
        else
        {
            Add(current => BufferAccess(current, "BufferLoadDword", 0, offset: 8, vectorData: 2, indexEnabled: true));
            keyRegister = 2;
        }

        Add(current => Vop2(current, keyOperation, 6, Operand(KeyMask), Gen5Operand.Vector(keyRegister)));
        for (uint index = 0; index < 4; index++)
        {
            var register = 24 + index;
            Add(current => MoveScalar(current, register, 0));
        }

        for (var loop = 0; loop < loops; loop++)
        {
            Add(current => ReadFirstLane(current, 8, 6));
            Add(current => Sop2(current, "SLshlB32", 9, Gen5Operand.Scalar(8), Operand(5)));
            Add(current => ScalarBufferLoad(current, 4, destination: 16, count: 8, dynamicOffsetRegister: 9));
            Add(current => Image(current, "ImageSample", 16, 24));
        }

        Add(EndProgram);
        return Program([.. instructions]);
    }

    private static uint[] Descriptor(uint variant)
    {
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        descriptor[0] += variant * 0x100;
        return descriptor;
    }

    private static uint[] KeyBufferUserData(uint stride, uint records, uint word3 = 0) =>
        [0x1000, stride << 16, records, word3, 0x2000, 0, 0x100, 0];

    [Fact]
    public void WaterfallKeyFromABufferPlansAnIndirectImage()
    {
        var plan = Extract(WaterfallProgram());
        Assert.Single(plan.Info.Images);
        var access = Assert.Single(plan.IndirectImages);
        Assert.True(access.KeyIsAddressOffset);
        Assert.Equal(ScalarValueKind.ScalarBufferWord, access.Key.Kind);

        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.LaneKeys!;
        var source = Assert.Single(selector.Sources);
        Assert.True(source.Indexed);
        Assert.False(source.Formatted);
        Assert.Equal(8u, source.OffsetBytes);
        Assert.Equal(0u, source.Component);
        Assert.Equal(KeyMask, source.Mask);
        Assert.Equal(0u, selector.TableOffset);
    }

    [Fact]
    public void EveryKeyRecordSelectsItsHeapDescriptor()
    {
        var plan = Extract(WaterfallProgram());
        var memory = ResourceTrackerTests.LinearMemory();
        // Three 16-byte records with the key at byte 8; the high bits are masked off.
        memory.At(0x1000 + 8) = 0x00F0_0001;
        memory.At(0x1010 + 8) = 2;
        memory.At(0x1020 + 8) = 1;
        for (uint key = 0; key < 3; key++)
            ResourceTrackerTests.WriteImage(memory, 0x2000 + key * 32, Descriptor(key));
        ResourceTrackerTests.WriteImage(memory, 0x2000 + 7 * 32, Descriptor(7));

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(KeyBufferUserData(16, 3), readCleanMemory: memory.Read),
            ref snapshot, ref specialization));

        Assert.Equal(3, snapshot.Images.Length);
        Assert.Equal(Descriptor(0), snapshot.Images[0]);
        Assert.Contains(snapshot.Images, image => image.SequenceEqual(Descriptor(1)));
        Assert.Contains(snapshot.Images, image => image.SequenceEqual(Descriptor(2)));
        Assert.DoesNotContain(snapshot.Images, image => image.SequenceEqual(Descriptor(7)));

        var mapping = specialization.Images[0].IndirectMappingOffset;
        var table = snapshot.FlattenedResourceTable;
        Assert.Equal(3u, table[mapping]);
        Assert.Equal(new uint[] { 0, 32, 64 }, new[] { table[mapping + 1], table[mapping + 3], table[mapping + 5] });

        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(3, applied.Info.Images[0].IndirectResources.Count);
    }

    [Fact]
    public void WaterfallLoopsOverTheSameKeyShareOneImage()
    {
        var plan = Extract(WaterfallProgram(loops: 2));
        Assert.Single(plan.Info.Images);
        Assert.Equal(2, plan.IndirectImages.Count);
        Assert.NotEqual(plan.IndirectImages[0].Key.MemoryIndex, plan.IndirectImages[1].Key.MemoryIndex);
    }

    [Fact]
    public void FormattedKeysFollowTheDescriptorFormatAndSelectConstants()
    {
        var plan = Extract(WaterfallProgram(formatted: true));
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.LaneKeys!;
        var source = Assert.Single(selector.Sources);
        Assert.True(source.Formatted);
        Assert.Equal(1u, source.Component);
        Assert.Equal(new uint[] { 0 }, selector.Constants);

        var memory = ResourceTrackerTests.LinearMemory();
        // 32_32 UINT records: x is not a key, y is.
        memory.At(0x1000) = 7;
        memory.At(0x1004) = 3;
        memory.At(0x1008) = 7;
        memory.At(0x100C) = 0x0010_0002;
        foreach (var key in new uint[] { 0, 2, 3, 7 })
            ResourceTrackerTests.WriteImage(memory, 0x2000 + key * 32, Descriptor(key));

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan,
            Inputs(KeyBufferUserData(8, 2, IdentitySwizzle | (Format32x2Uint << 12)), readCleanMemory: memory.Read),
            ref snapshot, ref specialization));

        Assert.Equal(3, snapshot.Images.Length);
        Assert.Contains(snapshot.Images, image => image.SequenceEqual(Descriptor(2)));
        Assert.Contains(snapshot.Images, image => image.SequenceEqual(Descriptor(3)));
        Assert.DoesNotContain(snapshot.Images, image => image.SequenceEqual(Descriptor(7)));
    }

    [Fact]
    public void KeyArithmeticOutsideTheProvenShapesIsStillRejected()
    {
        var error = Assert.Throws<ResourcePlanException>(() => Extract(WaterfallProgram(keyOperation: "VAddU32")));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    [Fact]
    public void AKeyTableTooLargeToEnumerateIsRejectable()
    {
        var plan = Extract(WaterfallProgram());
        var memory = ResourceTrackerTests.LinearMemory();
        ResourceTrackerTests.WriteImage(memory, 0x2000, Descriptor(0));
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs(KeyBufferUserData(16, 1u << 20), readCleanMemory: memory.Read),
            ref snapshot, ref specialization, out var failure));
        Assert.Equal(ResourceMaterializationFailure.UnresolvedImageKeys, failure);
    }
}
