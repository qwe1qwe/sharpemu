// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadThreadObjectTests
{
    [Fact]
    public void ThreadObjectStartsWithItsNonZeroThreadId()
    {
        var first = KernelPthreadState.CreateThreadHandle("tid-test-a");
        var second = KernelPthreadState.CreateThreadHandle("tid-test-b");

        Assert.True(KernelPthreadState.TryGetThreadIdentity(first, out var firstIdentity));
        Assert.True(KernelPthreadState.TryGetThreadIdentity(second, out var secondIdentity));

        var firstTid = Marshal.ReadInt64((nint)first, KernelPthreadState.ThreadObjectTidOffset);
        var secondTid = Marshal.ReadInt64((nint)second, KernelPthreadState.ThreadObjectTidOffset);
        Assert.NotEqual(0L, firstTid);
        Assert.NotEqual(firstTid, secondTid);
        Assert.Equal((long)firstIdentity.UniqueId, firstTid);
        Assert.Equal((long)secondIdentity.UniqueId, secondTid);
    }
}
