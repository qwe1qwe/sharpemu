// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Threading;

namespace SharpEmu.Libs.Ampr;

// APR and AMM command buffers run on their own hardware queues: a submit returns at once
// and a wait-on-address record stalls only that queue. Running a submission inline is
// fine until it reaches a wait whose condition does not hold yet; the remainder then
// moves to a background worker, so the submitting thread can go on and later produce
// the value the queue waits for. Marvel's Wolverine (PPSA03671) submits an APR read that
// waits for a counter written by an AMM buffer the main thread has not submitted yet,
// while the submitting thread holds a lock the main thread needs.
public static partial class AmprExports
{
    // Submissions with the same key run in submission order.
    public const ulong AmmQueueKeyBase = 0x1_0000_0000UL;

    private static readonly object _deferredGate = new();
    private static readonly Dictionary<ulong, Queue<AmprExecution>> _deferredQueues = new();
    private static Thread? _deferredWorker;

    public sealed class AmprExecution
    {
        private readonly ManualResetEventSlim _done = new(false);

        internal AmprExecution(
            CpuContext workerContext,
            ulong commandBuffer,
            ulong buffer,
            ulong writeOffset,
            ulong queueKey,
            List<(ulong Offset, int Kind, int Index)> commands,
            ReadFileCommand[] readCommands,
            KernelEventCommand[] kernelEventCommands,
            WriteAddressCommand[] writeAddressCommands,
            WaitAddressCommand[] waitAddressCommands,
            AmmCommand[] ammCommands)
        {
            WorkerContext = workerContext;
            CommandBuffer = commandBuffer;
            Buffer = buffer;
            WriteOffset = writeOffset;
            QueueKey = queueKey;
            Commands = commands;
            ReadCommands = readCommands;
            KernelEventCommands = kernelEventCommands;
            WriteAddressCommands = writeAddressCommands;
            WaitAddressCommands = waitAddressCommands;
            AmmCommands = ammCommands;
        }

        public ulong CommandBuffer { get; }
        public ulong QueueKey { get; }
        public int ExecutionResult { get; internal set; } = (int)OrbisGen2Result.ORBIS_GEN2_OK;
        public uint ErrorOffset { get; internal set; }
        public bool IsCompleted => _done.IsSet;

        internal CpuContext WorkerContext { get; }
        internal ulong Buffer { get; }
        internal ulong WriteOffset { get; }
        internal List<(ulong Offset, int Kind, int Index)> Commands { get; }
        internal int Next { get; set; }
        internal Action<CpuContext, AmprExecution>? Completed { get; set; }
        internal ReadFileCommand[] ReadCommands { get; }
        internal KernelEventCommand[] KernelEventCommands { get; }
        internal WriteAddressCommand[] WriteAddressCommands { get; }
        internal WaitAddressCommand[] WaitAddressCommands { get; }
        internal AmmCommand[] AmmCommands { get; }

        public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

        public void Wait() => _done.Wait();

        internal void MarkCompleted() => _done.Set();
    }

    // Submits a command buffer to the queue named by queueKey. When the result is known
    // at once, pending is null and the outputs hold it. Otherwise pending is the queued
    // execution: completed runs on the worker after its last command.
    public static int SubmitCommandBuffer(
        CpuContext ctx,
        ulong commandBuffer,
        ulong queueKey,
        Action<CpuContext, AmprExecution>? completed,
        out AmprExecution? pending,
        out int executionResult,
        out uint errorOffset)
    {
        pending = null;
        executionResult = (int)OrbisGen2Result.ORBIS_GEN2_OK;
        errorOffset = 0;
        var prepared = TryPrepareExecution(ctx, commandBuffer, queueKey, out var execution);
        if (prepared != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return prepared;
        }

        execution!.Completed = completed;
        lock (_deferredGate)
        {
            if (_deferredQueues.TryGetValue(queueKey, out var queue) && queue.Count != 0)
            {
                // Earlier work on this queue is still waiting; keep submission order.
                queue.Enqueue(execution);
                TraceAmpr(ctx, "submit_deferred", commandBuffer, queueKey, (ulong)queue.Count);
                pending = execution;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        if (AdvanceExecution(ctx, execution))
        {
            TraceAmpr(ctx, "complete", commandBuffer, execution.Buffer, execution.WriteOffset);
            execution.MarkCompleted();
            executionResult = execution.ExecutionResult;
            errorOffset = execution.ErrorOffset;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        lock (_deferredGate)
        {
            if (!_deferredQueues.TryGetValue(queueKey, out var queue))
            {
                queue = new Queue<AmprExecution>();
                _deferredQueues[queueKey] = queue;
            }

            queue.Enqueue(execution);
            EnsureDeferredWorkerLocked();
            Monitor.PulseAll(_deferredGate);
        }

        TraceAmpr(ctx, "submit_waiting", commandBuffer, queueKey, (ulong)execution.Next);
        pending = execution;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Number of submissions still queued behind an unsatisfied wait.
    public static int PendingSubmissionCount
    {
        get
        {
            lock (_deferredGate)
            {
                return _deferredQueues.Values.Sum(queue => queue.Count);
            }
        }
    }

    private static void EnsureDeferredWorkerLocked()
    {
        if (_deferredWorker is not null)
        {
            return;
        }

        _deferredWorker = new Thread(RunDeferredWorker)
        {
            IsBackground = true,
            Name = "SharpEmu-AprQueue",
        };
        _deferredWorker.Start();
    }

    private static void RunDeferredWorker()
    {
        var heads = new List<AmprExecution>();
        var idlePasses = 0;
        while (true)
        {
            heads.Clear();
            lock (_deferredGate)
            {
                while (_deferredQueues.Values.All(queue => queue.Count == 0))
                {
                    Monitor.Wait(_deferredGate);
                }

                foreach (var queue in _deferredQueues.Values)
                {
                    if (queue.Count != 0)
                    {
                        heads.Add(queue.Peek());
                    }
                }
            }

            var progressed = false;
            foreach (var execution in heads)
            {
                var before = execution.Next;
                bool done;
                try
                {
                    done = AdvanceExecution(execution.WorkerContext, execution);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] ampr deferred execution failed cmd=0x{execution.CommandBuffer:X16}: {exception.Message}");
                    execution.ExecutionResult = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    execution.Next = execution.Commands.Count;
                    done = true;
                }

                progressed |= execution.Next != before;
                if (!done)
                {
                    continue;
                }

                progressed = true;
                lock (_deferredGate)
                {
                    _deferredQueues[execution.QueueKey].Dequeue();
                }

                FinishDeferredExecution(execution);
            }

            if (progressed)
            {
                idlePasses = 0;
            }
            else if (++idlePasses < 64)
            {
                Thread.Yield();
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    private static void FinishDeferredExecution(AmprExecution execution)
    {
        TraceAmpr(execution.WorkerContext, "complete_deferred", execution.CommandBuffer, execution.Buffer, execution.WriteOffset);
        if (execution.ExecutionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] ampr deferred command failed cmd=0x{execution.CommandBuffer:X16} " +
                $"offset=0x{execution.ErrorOffset:X} result=0x{execution.ExecutionResult:X8}");
        }

        try
        {
            execution.Completed?.Invoke(execution.WorkerContext, execution);
        }
        finally
        {
            execution.MarkCompleted();
        }
    }
}
