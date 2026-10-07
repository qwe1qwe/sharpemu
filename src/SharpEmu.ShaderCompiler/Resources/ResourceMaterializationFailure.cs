// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public enum ResourceMaterializationFailure
{
    None,
    Other,
    IncompatibleImageCandidates,
    ImageCapacityExceeded,
    // A per-lane descriptor key comes from a table too large or in a layout the host
    // cannot enumerate; the access is rejected like an over-capacity one.
    UnresolvedImageKeys,
}
