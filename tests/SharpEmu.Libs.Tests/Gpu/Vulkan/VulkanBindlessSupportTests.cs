// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class VulkanBindlessSupportTests
{
    [Fact]
    public void FullDescriptorIndexingEnablesSampledAndStorageArrays()
    {
        var support = VulkanBindlessSupport.Decide(true, true, true, true, 1_048_576, 65_536, disabled: false);
        Assert.True(support.Available);
        Assert.True(support.StorageImages);
        Assert.Equal(1_048_576u, support.MaxSampledImagesPerStage);
        Assert.Equal(65_536u, support.MaxStorageImagesPerStage);
    }

    [Fact]
    public void StorageArraysAreOptional()
    {
        var support = VulkanBindlessSupport.Decide(true, true, true, false, 4096, 4096, disabled: false);
        Assert.True(support.Available);
        Assert.False(support.StorageImages);
        Assert.Equal(0u, support.MaxStorageImagesPerStage);
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void AMissingRequirementOrTheSwitchDisablesBindless(bool runtimeArray, bool partiallyBound, bool sampledIndexing, bool disabled)
    {
        var support = VulkanBindlessSupport.Decide(runtimeArray, partiallyBound, sampledIndexing, true, 4096, 4096, disabled);
        Assert.False(support.Available);
        Assert.Equal("unavailable", support.Describe());
    }
}
