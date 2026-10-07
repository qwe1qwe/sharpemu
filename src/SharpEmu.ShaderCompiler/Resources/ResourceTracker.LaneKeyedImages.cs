// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceTracker
{
    private const uint LaneKeyedImageShift = 5;
    private const int MaxLaneKeyDefinitions = 256;

    // Bindless lookups through a waterfall loop:
    //
    //   buffer_load_dword v79, v68, s[8:11] idxen offset:92   ; per-lane record of a light/probe list
    //   v_and_b32 v82, 0xfffff, v79                             ; descriptor key
    // loop:
    //   v_readfirstlane_b32 s106, v82
    //   v_cmp_eq_u32 s63, s106, v82
    //   s_and_saveexec_b32 s107, s63
    //   s_lshl_b32 s106, s106, 5
    //   s_buffer_load_dwordx8 s[40:47], s[40:43], s106           ; T# from a heap V#
    //   image_sample ... s[40:47]
    //   s_andn2_b32 exec, s107, s63
    //   s_cbranch_execnz loop
    //
    // The key is lane data, so the scalar graph rightly leaves it undefined. What is
    // finite is the set of records the key can be loaded from: the host enumerates
    // every record of those buffers at run time and binds the heap descriptors the
    // keys select. The shader keeps computing its own key and searches that table.
    private bool TryMakeLaneKeyedImage(ScalarValue handle, uint pc, out IndirectImagePlan plan)
    {
        plan = null!;
        if (handle.Kind != ScalarValueKind.ImageHandle || handle.Operands.Length != 8)
            return false;

        var reads = new ScalarValue[8];
        var memoryIndices = new int[8];
        ScalarValue? heapHandle = null;
        ScalarValue? heapOffset = null;
        uint tableImmediate = 0;
        for (var dword = 0; dword < 8; dword++)
        {
            var read = handle.Operands[dword];
            var memory = ScalarReadMemory(read, out var memoryIndex);
            if (memory is null || memory.Kind != MemoryResourceKind.ScalarBuffer || !MemoryIndexBelongsTo(memoryIndex, read))
                return false;

            var componentOffset = (uint)dword * sizeof(uint);
            if (memory.Offset < componentOffset)
                return false;
            var immediate = memory.Offset - componentOffset;
            if (dword == 0)
                tableImmediate = immediate;
            else if (immediate != tableImmediate)
                return false;

            var currentHandle = read.Operands[0];
            if (currentHandle.Kind != ScalarValueKind.BufferHandle ||
                (heapHandle is not null && !ReferenceEquals(currentHandle, heapHandle) && !_graph.Equivalent(currentHandle, heapHandle)))
                return false;
            heapHandle = currentHandle;

            var offset = read.Operands[1];
            if (heapOffset is null)
                heapOffset = offset;
            else if (!ReferenceEquals(offset, heapOffset) && !_graph.Equivalent(offset, heapOffset))
                return false;

            reads[dword] = read;
            memoryIndices[dword] = memoryIndex;
        }

        if (heapHandle is null || heapOffset is null)
            return false;

        // The register offset may add a constant to the shifted key before the load.
        uint dynamicBase = 0;
        var scaled = heapOffset;
        if (scaled.Kind == ScalarValueKind.Operation && scaled.Operation == ScalarOperation.IAdd32 && scaled.Operands.Length == 2)
        {
            if (scaled.Operands[1].IsConstant)
            {
                dynamicBase = scaled.Operands[1].ConstantU32;
                scaled = scaled.Operands[0];
            }
            else if (scaled.Operands[0].IsConstant)
            {
                dynamicBase = scaled.Operands[0].ConstantU32;
                scaled = scaled.Operands[1];
            }
            else
            {
                return false;
            }
        }

        if (scaled.Kind != ScalarValueKind.Operation || scaled.Operation != ScalarOperation.ShiftLeft32 ||
            scaled.Operands.Length != 2 || !scaled.Operands[1].IsConstant ||
            scaled.Operands[1].ConstantU32 != LaneKeyedImageShift)
            return false;

        var key = scaled.Operands[0];
        if (key.Kind != ScalarValueKind.FirstLane)
            return false;

        var instructions = _graph.Program.Instructions;
        var firstLaneIndex = FindInstructionIndex(instructions, (uint)key.Payload);
        if (firstLaneIndex < 0 ||
            instructions[firstLaneIndex] is not { Opcode: "VReadfirstlaneB32", Sources.Count: 1 } firstLane ||
            firstLane.Sources[0] is not { Kind: Gen5OperandKind.VectorRegister } laneKey)
            return false;

        var leaves = new List<(Gen5ShaderInstruction Load, uint Component, uint Mask)>();
        var constants = new SortedSet<uint>();
        if (!TryCollectLaneKeys(instructions, firstLaneIndex, laneKey, uint.MaxValue, leaves, constants,
                new HashSet<(int, uint, uint)>()) || leaves.Count == 0)
            return false;

        var canSuppressMemoryReads = true;
        for (var dword = 0; dword < reads.Length; dword++)
        {
            if (!UsesOnly(reads[dword], [handle]))
                return false;
            canSuppressMemoryReads &= HasOnlyImageConsumers(_plan.Memory[memoryIndices[dword]], handle);
        }

        if (!MakeRuntimeBufferSource(heapHandle, pc, out var heapSourceIndex, out var heapSource))
            return false;

        var sources = new SortedSet<LaneKeySource>(Comparer<LaneKeySource>.Create(CompareLaneKeySources));
        foreach (var (load, component, mask) in leaves)
        {
            if (!_plan.Memory.TryGetIndex(load.Pc, 0, out var loadIndex) || loadIndex >= _plan.Accesses.Length ||
                _plan.Memory[loadIndex] is not { Kind: MemoryResourceKind.Buffer, Access: MemoryAccess.Read, OffsetEnabled: false } loadMemory ||
                _plan.Accesses[loadIndex]?.Handle is not { } bufferHandle ||
                !MakeRuntimeBufferSource(bufferHandle, load.Pc, out var bufferSourceIndex, out _) ||
                load.Control is not Gen5BufferMemoryControl control)
                return false;

            sources.Add(new LaneKeySource(
                bufferSourceIndex,
                loadMemory.Offset,
                component,
                loadMemory.Formatted,
                loadMemory.IndexEnabled,
                mask,
                control.Typed ? control.TypedFormat : 0));
        }

        var tableOffset = unchecked(tableImmediate + dynamicBase);
        var selector = new LaneKeyedImageSelector(tableOffset, dynamicBase)
        {
            Sources = [.. sources],
            Constants = [.. constants],
        };

        // Equal heaps and key sources intern to one source, so every waterfall loop that
        // reads the same key shares one root image and one candidate table.
        var imageSource = new DescriptorSource
        {
            Dwords = [.. heapSource.Dwords, .. heapSource.Dwords],
            IndirectImage = new IndirectImageSelector(0, heapSourceIndex, 0, 0, 0)
            {
                TableOffset = tableOffset,
                DynamicOffsetBase = dynamicBase,
                LaneKeys = selector,
            },
        };

        plan = new IndirectImagePlan
        {
            Handle = handle,
            Source = InternSource(imageSource),
            Key = reads[0],
            KeyIsAddressOffset = true,
            HeapSource = heapSourceIndex,
            SuppressMemoryReads = canSuppressMemoryReads,
            Memory = memoryIndices,
            Reads = reads,
        };
        return true;
    }

    // Walks the straight-line definitions of a lane key back to the buffer loads and
    // constants it can hold. Anything but a mask, a select, a move or a whole-dword
    // indexed buffer load leaves the key unproven.
    private static bool TryCollectLaneKeys(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int before,
        Gen5Operand value,
        uint mask,
        List<(Gen5ShaderInstruction Load, uint Component, uint Mask)> leaves,
        SortedSet<uint> constants,
        HashSet<(int Before, uint Register, uint Mask)> visited)
    {
        if (TryGetConstant(value, out var constant))
        {
            constants.Add(constant & mask);
            return true;
        }

        if (value.Kind != Gen5OperandKind.VectorRegister)
            return false;

        if (!visited.Add((before, value.Value, mask)))
            return true;
        if (visited.Count > MaxLaneKeyDefinitions)
            return false;

        var definitionIndex = FindLastDefinition(instructions, before, value);
        if (definitionIndex < 0)
            return false;

        var definition = instructions[definitionIndex];
        if (definition.Control is Gen5BufferMemoryControl buffer)
        {
            if (!IsLaneKeyLoad(definition.Opcode) || buffer.OffsetEnabled)
                return false;

            var component = -1;
            for (var index = 0; index < definition.Destinations.Count; index++)
            {
                if (definition.Destinations[index] == value)
                {
                    component = index;
                    break;
                }
            }

            if (component < 0)
                return false;

            leaves.Add((definition, (uint)component, mask));
            return true;
        }

        if (!HasPlainIntegerOperands(definition))
            return false;

        switch (definition.Opcode)
        {
            case "VMovB32" when definition.Sources.Count == 1:
                return TryCollectLaneKeys(instructions, definitionIndex, definition.Sources[0], mask, leaves, constants, visited);

            case "VAndB32" when definition.Sources.Count == 2:
            {
                if (TryGetConstant(definition.Sources[0], out var left))
                    return TryCollectLaneKeys(instructions, definitionIndex, definition.Sources[1], mask & left, leaves, constants, visited);
                if (TryGetConstant(definition.Sources[1], out var right))
                    return TryCollectLaneKeys(instructions, definitionIndex, definition.Sources[0], mask & right, leaves, constants, visited);
                return false;
            }

            // Either input can be selected per lane: both are possible keys.
            case "VCndmaskB32" when definition.Sources.Count >= 2:
                return TryCollectLaneKeys(instructions, definitionIndex, definition.Sources[0], mask, leaves, constants, visited) &&
                    TryCollectLaneKeys(instructions, definitionIndex, definition.Sources[1], mask, leaves, constants, visited);

            default:
                return false;
        }
    }

    // Whole-dword loads keep the stored bits; sub-dword and D16 loads would need their
    // own extension rules, so they are not lane-key sources.
    private static bool IsLaneKeyLoad(string opcode) =>
        opcode is "BufferLoadDword" or "BufferLoadDwordx2" or "BufferLoadDwordx3" or "BufferLoadDwordx4" or
            "BufferLoadFormatX" or "BufferLoadFormatXy" or "BufferLoadFormatXyz" or "BufferLoadFormatXyzw" or
            "TBufferLoadFormatX" or "TBufferLoadFormatXy" or "TBufferLoadFormatXyz" or "TBufferLoadFormatXyzw";

    // Source modifiers would change the selected bits: SDWA byte/word selects, DPP lane
    // shuffles and VOP3 abs/neg all make the value something other than a stored key.
    private static bool HasPlainIntegerOperands(Gen5ShaderInstruction instruction) => instruction.Control switch
    {
        null => true,
        Gen5Vop3Control vop3 => vop3.AbsoluteMask == 0 && vop3.NegateMask == 0 && vop3.OutputModifier == 0 && !vop3.Clamp,
        Gen5SdwaControl sdwa => sdwa.Source0Select == 6 && sdwa.Source1Select == 6 && sdwa.DestinationSelect == 6 &&
            !sdwa.Source0SignExtend && !sdwa.Source1SignExtend && sdwa.AbsoluteMask == 0 && sdwa.NegateMask == 0 &&
            sdwa.OutputModifier == 0 && !sdwa.Clamp,
        _ => false,
    };

    private static int CompareLaneKeySources(LaneKeySource? left, LaneKeySource? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var order = left.BufferSource.CompareTo(right.BufferSource);
        if (order == 0) order = left.OffsetBytes.CompareTo(right.OffsetBytes);
        if (order == 0) order = left.Component.CompareTo(right.Component);
        if (order == 0) order = left.Mask.CompareTo(right.Mask);
        if (order == 0) order = left.Formatted.CompareTo(right.Formatted);
        if (order == 0) order = left.Indexed.CompareTo(right.Indexed);
        if (order == 0) order = left.TypedFormat.CompareTo(right.TypedFormat);
        return order;
    }
}
