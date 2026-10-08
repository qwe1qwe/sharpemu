// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Vulkan;

// Whether the device can bind a guest descriptor heap as one large image array the
// shader indexes per lane (descriptor indexing). Guest shaders select textures from a
// heap through a waterfall loop; with this support the array holds the whole heap
// instead of a bounded candidate list.
public readonly record struct VulkanBindlessSupport(
    bool SampledImages,
    bool StorageImages,
    uint MaxSampledImagesPerStage,
    uint MaxStorageImagesPerStage)
{
    public const string DisableVariable = "SHARPEMU_DISABLE_BINDLESS";

    public static VulkanBindlessSupport None => default;

    public bool Available => SampledImages;

    // The features one heap array needs: a runtime-sized, partially bound array of
    // sampled (or storage) images indexed by a non-uniform value. Update-after-bind is
    // not needed: a heap set is written once before its first use and then only reused.
    public static VulkanBindlessSupport Decide(
        bool runtimeDescriptorArray,
        bool partiallyBound,
        bool sampledNonUniformIndexing,
        bool storageNonUniformIndexing,
        uint maxPerStageSampledImages,
        uint maxPerStageStorageImages,
        bool disabled)
    {
        if (disabled || !runtimeDescriptorArray || !partiallyBound || !sampledNonUniformIndexing)
        {
            return None;
        }

        return new VulkanBindlessSupport(
            SampledImages: true,
            StorageImages: storageNonUniformIndexing,
            MaxSampledImagesPerStage: maxPerStageSampledImages,
            MaxStorageImagesPerStage: storageNonUniformIndexing ? maxPerStageStorageImages : 0);
    }

    public static bool DisabledByEnvironment() =>
        string.Equals(Environment.GetEnvironmentVariable(DisableVariable), "1", StringComparison.Ordinal);

    public string Describe() => Available
        ? $"sampled=yes storage={(StorageImages ? "yes" : "no")} max_sampled_per_stage={MaxSampledImagesPerStage} " +
          $"max_storage_per_stage={MaxStorageImagesPerStage}"
        : "unavailable";
}
