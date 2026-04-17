using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;

public static class PulsarTransformEngine
{
    private static readonly ConcurrentDictionary<(int size, int prevSize, int nextSize), float[]> WindowCache = new();
    private static readonly ConcurrentDictionary<int, ComplexFftPlan> ComplexFftPlanCache = new();
    private static readonly ConcurrentDictionary<int, Dct4Plan> Dct4PlanCache = new();

    private sealed class ComplexFftPlan
    {
        public required int Size { get; init; }
        public required int[] BitReverse { get; init; }
        public required double[] TwiddleReal { get; init; }
        public required double[] TwiddleImag { get; init; }
    }

    private sealed class Dct4Plan
    {
        public required int Size { get; init; }
        public required int FftSize { get; init; }
        public required ComplexFftPlan FftPlan { get; init; }
        public required double[] PreCos { get; init; }
        public required double[] PreSin { get; init; }
        public required double[] KernelFftReal { get; init; }
        public required double[] KernelFftImag { get; init; }
    }

    private sealed class TransformWorkspace
    {
        public float[] FoldedInput = Array.Empty<float>();
        public float[] FoldedOutput = Array.Empty<float>();
        public float[] Spectrum = Array.Empty<float>();
        public float[] TimeBlock = Array.Empty<float>();
        public double[] FftReal = Array.Empty<double>();
        public double[] FftImag = Array.Empty<double>();

        public void EnsureTransformSize(int size)
        {
            if (FoldedInput.Length < size)
            {
                FoldedInput = new float[size];
            }

            if (FoldedOutput.Length < size)
            {
                FoldedOutput = new float[size];
            }

            if (Spectrum.Length < size)
            {
                Spectrum = new float[size];
            }

            int blockSize = size * 2;
            if (TimeBlock.Length < blockSize)
            {
                TimeBlock = new float[blockSize];
            }
        }

        public void EnsureFftSize(int size)
        {
            if (FftReal.Length < size)
            {
                FftReal = new double[size];
            }

            if (FftImag.Length < size)
            {
                FftImag = new double[size];
            }
        }
    }

    static PulsarTransformEngine()
    {
        PulsarBlockLadder.Validate();
    }

    public static float[] Process(float[] input, PulsarPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var framePlans = planner.PlanSong(input, null);
        return RenderPlannerCompatiblePath(input, framePlans);
    }

    public static (float[] Output, List<PulsarFramePlan> Plans) ProcessWithPlans(float[] input, PulsarPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var framePlans = planner.PlanSong(input, null);
        return (RenderPlannerCompatiblePath(input, framePlans), framePlans);
    }

    public static float[] ProcessLegacyPlanner(float[] input, PulsarPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var framePlans = planner.PlanLegacyRenderSong(input, null);
        return RenderExplicitPlannerPath(input, framePlans);
    }

    public static (float[] Output, double PlannerSeconds, double RenderSeconds) ProcessLegacyPlannerWithTimings(float[] input, PulsarPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var plannerStopwatch = Stopwatch.StartNew();
        var framePlans = planner.PlanLegacyRenderSong(input, null);
        plannerStopwatch.Stop();

        var renderStopwatch = Stopwatch.StartNew();
        float[] output = RenderExplicitPlannerPath(input, framePlans);
        renderStopwatch.Stop();

        return (output, plannerStopwatch.Elapsed.TotalSeconds, renderStopwatch.Elapsed.TotalSeconds);
    }

    public static float[] ProcessWithBitAllocation(
        float[] input,
        IReadOnlyList<PulsarFramePlan> framePlans,
        IReadOnlyList<PulsarFrameAllocation> allocations,
        IReadOnlyList<Pulsar.Psycho.PulsarPsychoResult> psychoFrames)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(framePlans);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(psychoFrames);

        if (framePlans.Count != allocations.Count || framePlans.Count != psychoFrames.Count)
        {
            throw new ArgumentException("Plans, allocations, and psycho frames must have the same count.");
        }

        return RenderSequentialQuantizedPath(input, framePlans, allocations, psychoFrames);
    }

    public static float[] ProcessLegacy(float[] input, int blockSize = PulsarBlockLadder.DefaultBlockSize)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!PulsarBlockLadder.IsValidBlockSize(blockSize))
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Legacy block size must be a valid ladder step.");
        }

        return RenderConstantBlockPath(input, blockSize);
    }

    private static float[] RenderConstantBlockPath(float[] input, int blockSize)
    {
        if (input.Length == 0) return Array.Empty<float>();

        int hopSize = blockSize / 2;
        int alignedInputLength = ((input.Length + hopSize - 1) / hopSize) * hopSize;
        int transformSize = blockSize / 2;

        float[] paddedInput = new float[alignedInputLength + hopSize * 2];
        Array.Copy(input, 0, paddedInput, hopSize, input.Length);

        float[] output = new float[paddedInput.Length + blockSize];
        float[] overlapBuffer = new float[blockSize];
        float[] window = GetWindow(blockSize, blockSize, blockSize);
        Dct4Plan plan = GetDct4Plan(transformSize);
        var workspace = new TransformWorkspace();
        workspace.EnsureTransformSize(transformSize);
        workspace.EnsureFftSize(plan.FftSize);

        int currentInputPos = 0;
        int currentOutputPos = 0;
        int overlapCount = 0;
        while (currentInputPos + blockSize <= paddedInput.Length)
        {
            BuildMdctFoldedInput(paddedInput, currentInputPos, window, workspace.FoldedInput, blockSize);
            ComputeDct4(workspace.FoldedInput, workspace.Spectrum, plan, workspace);
            ComputeInverseDct4(workspace.Spectrum, workspace.FoldedOutput, plan, workspace);
            OverlapAddSynthesizedBlock(workspace.FoldedOutput, transformSize, window, output, currentOutputPos, overlapBuffer, hopSize, hopSize, overlapCount);

            currentInputPos += hopSize;
            currentOutputPos += hopSize;
            overlapCount = hopSize;
        }

        float[] trimmed = new float[input.Length];
        Array.Copy(output, hopSize, trimmed, 0, input.Length);
        return trimmed;
    }

    private static float[] RenderPlannerCompatiblePath(float[] input, IReadOnlyList<PulsarFramePlan> framePlans)
    {
        if (framePlans.Count == 0 || input.Length == 0) return Array.Empty<float>();

        int initialBlockSize = framePlans[0].BlockSize;
        int initialHop = initialBlockSize / 2;
        int alignedInputLength = ((input.Length + PulsarBlockLadder.ControlHopSize - 1) / PulsarBlockLadder.ControlHopSize) * PulsarBlockLadder.ControlHopSize;

        float[] paddedInput = new float[alignedInputLength + initialHop * 2];
        Array.Copy(input, 0, paddedInput, initialHop, input.Length);

        float[] output = new float[paddedInput.Length + PulsarBlockLadder.MaxBlockSize];
        float[] overlapBuffer = new float[PulsarBlockLadder.MaxBlockSize];
        var workspace = new TransformWorkspace();

        int currentInputPos = 0;
        int currentOutputPos = 0;
        int overlapCount = 0;

        while (currentInputPos < paddedInput.Length - PulsarBlockLadder.MinBlockSize)
        {
            int unpaddedPos = currentInputPos - initialHop;
            PulsarFramePlan currentPlan = ResolvePlannerRenderPlan(framePlans, unpaddedPos);
            int currentBlockSize = currentPlan.BlockSize;
            int previousBlockSize = currentPlan.PreviousBlockSize;
            int nextBlockSize = currentPlan.NextBlockSize;

            if (currentInputPos + currentBlockSize > paddedInput.Length)
            {
                currentBlockSize = PulsarBlockLadder.MinBlockSize;
                while (currentInputPos + currentBlockSize > paddedInput.Length && currentBlockSize > 2)
                    currentBlockSize /= 2;
                if (currentBlockSize < PulsarBlockLadder.MinBlockSize) break;

                previousBlockSize = Math.Min(previousBlockSize, currentBlockSize);
                nextBlockSize = Math.Min(nextBlockSize, currentBlockSize);
            }

            ComputeAsymmetricOverlap(currentBlockSize, previousBlockSize, nextBlockSize, out int leftOverlap, out int rightOverlap);
            int flatEnd = currentBlockSize - rightOverlap;

            float[] window = GetWindow(currentBlockSize, previousBlockSize, nextBlockSize);

            Dct4Plan plan = GetDct4Plan(currentBlockSize / 2);
            workspace.EnsureTransformSize(plan.Size);
            workspace.EnsureFftSize(plan.FftSize);

            BuildMdctFoldedInput(paddedInput, currentInputPos, window, workspace.FoldedInput, currentBlockSize);
            ComputeDct4(workspace.FoldedInput, workspace.Spectrum, plan, workspace);
            ComputeInverseDct4(workspace.Spectrum, workspace.FoldedOutput, plan, workspace);

            if (leftOverlap == plan.Size && rightOverlap == plan.Size)
            {
                OverlapAddSynthesizedBlock(workspace.FoldedOutput, plan.Size, window, output, currentOutputPos, overlapBuffer, leftOverlap, flatEnd, overlapCount);
            }
            else
            {
                ExpandFoldedImdct(workspace.FoldedOutput, plan.Size, workspace.TimeBlock);
                ApplyWindow(workspace.TimeBlock, currentBlockSize, window);
                OverlapAddMaterializedBlock(workspace.TimeBlock, currentBlockSize, output, currentOutputPos, overlapBuffer, leftOverlap, flatEnd, overlapCount);
            }

            int hopSize = flatEnd;
            currentInputPos += hopSize;
            currentOutputPos += hopSize;
            overlapCount = rightOverlap;
        }

        float[] trimmed = new float[input.Length];
        int copyLength = Math.Min(input.Length, output.Length - initialHop);
        if (copyLength > 0)
            Array.Copy(output, initialHop, trimmed, 0, copyLength);

        return trimmed;
    }

    private static float[] RenderExplicitPlannerPath(float[] input, IReadOnlyList<PulsarFramePlan> framePlans)
    {
        if (framePlans.Count == 0 || input.Length == 0) return Array.Empty<float>();

        int initialBlockSize = framePlans[0].BlockSize;
        int initialHop = initialBlockSize / 2;
        int alignQuantum = PulsarBlockLadder.MinBlockSize / 2;
        int alignedInputLength = ((input.Length + alignQuantum - 1) / alignQuantum) * alignQuantum;
        int targetOutputCoverage = alignedInputLength + initialHop;

        float[] paddedInput = new float[alignedInputLength + (PulsarBlockLadder.MaxBlockSize * 2)];
        Array.Copy(input, 0, paddedInput, initialHop, input.Length);

        float[] output = new float[paddedInput.Length + PulsarBlockLadder.MaxBlockSize];
        float[] overlapBuffer = new float[PulsarBlockLadder.MaxBlockSize];
        var workspace = new TransformWorkspace();

        int currentInputPos = 0;
        int currentOutputPos = 0;
        int overlapCount = 0;

        foreach (PulsarFramePlan framePlan in framePlans)
        {
            int currentBlockSize = framePlan.BlockSize;
            int previousBlockSize = framePlan.PreviousBlockSize;
            int nextBlockSize = framePlan.NextBlockSize;

            if (currentInputPos + currentBlockSize > paddedInput.Length)
            {
                break;
            }

            ComputeAsymmetricOverlap(currentBlockSize, previousBlockSize, nextBlockSize, out int leftOverlap, out int rightOverlap);
            int flatEnd = currentBlockSize - rightOverlap;
            float[] window = GetWindow(currentBlockSize, previousBlockSize, nextBlockSize);

            Dct4Plan plan = GetDct4Plan(currentBlockSize / 2);
            workspace.EnsureTransformSize(plan.Size);
            workspace.EnsureFftSize(plan.FftSize);

            BuildMdctFoldedInput(paddedInput, currentInputPos, window, workspace.FoldedInput, currentBlockSize);
            ComputeDct4(workspace.FoldedInput, workspace.Spectrum, plan, workspace);
            ComputeInverseDct4(workspace.Spectrum, workspace.FoldedOutput, plan, workspace);

            if (leftOverlap == plan.Size && rightOverlap == plan.Size)
            {
                OverlapAddSynthesizedBlock(workspace.FoldedOutput, plan.Size, window, output, currentOutputPos, overlapBuffer, leftOverlap, flatEnd, overlapCount);
            }
            else
            {
                ExpandFoldedImdct(workspace.FoldedOutput, plan.Size, workspace.TimeBlock);
                ApplyWindow(workspace.TimeBlock, currentBlockSize, window);
                OverlapAddMaterializedBlock(workspace.TimeBlock, currentBlockSize, output, currentOutputPos, overlapBuffer, leftOverlap, flatEnd, overlapCount);
            }

            currentInputPos += flatEnd;
            currentOutputPos += flatEnd;
            overlapCount = rightOverlap;

            if (currentOutputPos >= targetOutputCoverage)
            {
                break;
            }
        }

        float[] trimmed = new float[input.Length];
        int copyLength = Math.Min(input.Length, output.Length - initialHop);
        if (copyLength > 0)
        {
            Array.Copy(output, initialHop, trimmed, 0, copyLength);
        }

        return trimmed;
    }

    private static PulsarFramePlan ResolvePlannerRenderPlan(IReadOnlyList<PulsarFramePlan> framePlans, int unpaddedPos)
    {
        int controlHop = PulsarBlockLadder.ControlHopSize;
        int segmentIndex = unpaddedPos >= 0 ? (unpaddedPos / controlHop) : 0;
        segmentIndex = Math.Clamp(segmentIndex, 0, framePlans.Count - 1);

        PulsarFramePlan resolvedPlan = framePlans[segmentIndex];
        for (int iteration = 0; iteration < 2; iteration++)
        {
            int centerPos = unpaddedPos + (resolvedPlan.BlockSize / 2);
            int centerSegmentIndex = centerPos >= 0 ? (centerPos / controlHop) : 0;
            centerSegmentIndex = Math.Clamp(centerSegmentIndex, 0, framePlans.Count - 1);

            PulsarFramePlan centeredPlan = framePlans[centerSegmentIndex];
            if (ReferenceEquals(centeredPlan, resolvedPlan))
            {
                break;
            }

            resolvedPlan = centeredPlan;
        }

        return resolvedPlan;
    }

    // --- DIE QUANTISIERTE SEQUENZIELLE MASTER-SCHLEIFE ---
    private static float[] RenderSequentialQuantizedPath(
        float[] input, 
        IReadOnlyList<PulsarFramePlan> framePlans,
        IReadOnlyList<PulsarFrameAllocation> allocations,
        IReadOnlyList<Pulsar.Psycho.PulsarPsychoResult> psychoFrames)
    {
        if (framePlans.Count == 0 || input.Length == 0) return Array.Empty<float>();

        int initialBlockSize = framePlans[0].BlockSize;
        int initialHop = initialBlockSize / 2;
        int alignQuantum = PulsarBlockLadder.MinBlockSize / 2;
        int alignedInputLength = ((input.Length + alignQuantum - 1) / alignQuantum) * alignQuantum;
        int targetOutputCoverage = alignedInputLength + initialHop;

        float[] paddedInput = new float[alignedInputLength + (PulsarBlockLadder.MaxBlockSize * 2)];
        Array.Copy(input, 0, paddedInput, initialHop, input.Length);

        float[] output = new float[paddedInput.Length + PulsarBlockLadder.MaxBlockSize];
        float[] overlapBuffer = new float[PulsarBlockLadder.MaxBlockSize];
        var workspace = new TransformWorkspace();

        int currentInputPos = 0;
        int currentOutputPos = 0;
        int overlapCount = 0;

        foreach (PulsarFramePlan framePlan in framePlans)
        {
            int currentBlockSize = framePlan.BlockSize;
            int previousBlockSize = framePlan.PreviousBlockSize;
            int nextBlockSize = framePlan.NextBlockSize;

            if (currentInputPos + currentBlockSize > paddedInput.Length)
            {
                break;
            }

            ComputeAsymmetricOverlap(currentBlockSize, previousBlockSize, nextBlockSize, out int leftOverlap, out int rightOverlap);
            int flatEnd = currentBlockSize - rightOverlap;

            float[] window = GetWindow(currentBlockSize, previousBlockSize, nextBlockSize);

            Dct4Plan plan = GetDct4Plan(currentBlockSize / 2);
            workspace.EnsureTransformSize(plan.Size);
            workspace.EnsureFftSize(plan.FftSize);

            BuildMdctFoldedInput(paddedInput, currentInputPos, window, workspace.FoldedInput, currentBlockSize);
            ComputeDct4(workspace.FoldedInput, workspace.Spectrum, plan, workspace);

            var quantContext = BuildQuantizationContext(currentInputPos, currentBlockSize, allocations, psychoFrames);
            PulsarQuantizer.QuantizeSpectrum(workspace.Spectrum, quantContext.PulseBudget, quantContext.Psycho);

            ComputeInverseDct4(workspace.Spectrum, workspace.FoldedOutput, plan, workspace);

            if (leftOverlap == plan.Size && rightOverlap == plan.Size)
            {
                OverlapAddSynthesizedBlock(workspace.FoldedOutput, plan.Size, window, output, currentOutputPos, overlapBuffer, leftOverlap, flatEnd, overlapCount);
            }
            else
            {
                ExpandFoldedImdct(workspace.FoldedOutput, plan.Size, workspace.TimeBlock);
                ApplyWindow(workspace.TimeBlock, currentBlockSize, window);
                OverlapAddMaterializedBlock(workspace.TimeBlock, currentBlockSize, output, currentOutputPos, overlapBuffer, leftOverlap, flatEnd, overlapCount);
            }

            currentInputPos += flatEnd;
            currentOutputPos += flatEnd;
            overlapCount = rightOverlap;

            if (currentOutputPos >= targetOutputCoverage)
            {
                break;
            }
        }

        float[] trimmed = new float[input.Length];
        int copyLength = Math.Min(input.Length, output.Length - initialHop);
        if (copyLength > 0)
            Array.Copy(output, initialHop, trimmed, 0, copyLength);

        return trimmed;
    }

    private static (int PulseBudget, Pulsar.Psycho.PulsarPsychoResult Psycho) BuildQuantizationContext(
        int offset,
        int blockSize,
        IReadOnlyList<PulsarFrameAllocation> allocations,
        IReadOnlyList<Pulsar.Psycho.PulsarPsychoResult> psychoFrames)
    {
        int controlHop = PulsarBlockLadder.ControlHopSize;
        int startSegment = Math.Clamp(offset / controlHop, 0, allocations.Count - 1);
        int endSegment = Math.Clamp((int)Math.Ceiling((offset + blockSize) / (double)controlHop) - 1, startSegment, allocations.Count - 1);
        int centerSegment = Math.Clamp(((offset + (blockSize / 2)) / controlHop), 0, psychoFrames.Count - 1);
        int pulseBudget = 0;

        for (int segmentIndex = startSegment; segmentIndex <= endSegment; segmentIndex++)
        {
            pulseBudget += allocations[segmentIndex].PulseBudget;
        }

        int segmentCount = Math.Max(1, endSegment - startSegment + 1);
        pulseBudget = Math.Max(1, (int)Math.Round(pulseBudget / (double)segmentCount));

        return (pulseBudget, psychoFrames[centerSegment]);
    }

    // --- ASYMMETRISCHE FENSTER GENERIERUNG (SIMD-Optimiert) ---
    private static float[] BuildAsymmetricWindow(int size, int prevSize, int nextSize)
    {
        ComputeAsymmetricOverlap(size, prevSize, nextSize, out int leftOverlap, out int rightOverlap);
        int flatEnd = size - rightOverlap;

        float[] window = new float[size];
        int vectorSize = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && vectorSize > 1)
        {
            float twoLeftOverlap = 2.0f * leftOverlap;
            float twoRightOverlap = 2.0f * rightOverlap;
            var piVector = new Vector<float>((float)Math.PI);
            var halfVector = new Vector<float>(0.5f);
            var leftDenom = new Vector<float>(twoLeftOverlap);
            var rightDenom = new Vector<float>(twoRightOverlap);
            float[] laneOffsetsArray = new float[vectorSize];
            for (int lane = 0; lane < vectorSize; lane++)
            {
                laneOffsetsArray[lane] = lane;
            }
            var laneOffsets = new Vector<float>(laneOffsetsArray);
            float[] temp = new float[vectorSize];

            int i = 0;
            for (; i <= leftOverlap - vectorSize && i < size; i += vectorSize)
            {
                var baseIndex = new Vector<float>(i) + laneOffsets;
                var arg = (baseIndex + halfVector) * (piVector / leftDenom);
                arg.CopyTo(temp);
                for (int lane = 0; lane < vectorSize; lane++)
                {
                    window[i + lane] = MathF.Sin(temp[lane]);
                }
            }
            for (; i < leftOverlap && i < size; i++)
            {
                window[i] = (float)Math.Sin(Math.PI * (i + 0.5f) / twoLeftOverlap);
            }

            for (int j = leftOverlap; j < flatEnd; j++)
            {
                window[j] = 1.0f;
            }

            i = 0;
            for (; i <= rightOverlap - vectorSize && flatEnd + i + vectorSize <= size; i += vectorSize)
            {
                var baseIndex = new Vector<float>(i) + laneOffsets;
                var arg = (baseIndex + halfVector) * (piVector / rightDenom);
                arg.CopyTo(temp);
                for (int lane = 0; lane < vectorSize; lane++)
                {
                    window[flatEnd + i + lane] = MathF.Cos(temp[lane]);
                }
            }
            for (; i < rightOverlap && flatEnd + i < size; i++)
            {
                window[flatEnd + i] = (float)Math.Cos(Math.PI * (i + 0.5f) / twoRightOverlap);
            }

            return window;
        }

        for (int i = 0; i < leftOverlap && i < size; i++)
        {
            window[i] = (float)Math.Sin(Math.PI * (i + 0.5f) / (2.0f * leftOverlap));
        }

        for (int i = leftOverlap; i < flatEnd; i++)
        {
            window[i] = 1.0f;
        }

        for (int i = 0; i < rightOverlap && flatEnd + i < size; i++)
        {
            window[flatEnd + i] = (float)Math.Cos(Math.PI * (i + 0.5f) / (2.0f * rightOverlap));
        }

        return window;
    }

    private static void ComputeAsymmetricOverlap(int currentBlockSize, int previousBlockSize, int nextBlockSize, out int leftOverlap, out int rightOverlap)
    {
        // FIX: Radikal vereinfacht.
        // MDCT-TDAC erfordert ZWINGEND, dass die Überlappung zwischen Block N und N+1 exakt gleich ist.
        // Der alte Clamp-Code hat bei 50% Overlap versehentlich um 1 Sample gekürzt und die Symmetrie zerstört.
        leftOverlap = Math.Min(previousBlockSize / 2, currentBlockSize / 2);
        rightOverlap = Math.Min(currentBlockSize / 2, nextBlockSize / 2);
    }

    private static float[] GetWindow(int size, int prevSize, int nextSize)
    {
        return WindowCache.GetOrAdd((size, prevSize, nextSize), _ => BuildAsymmetricWindow(size, prevSize, nextSize));
    }

    public static void ApplyWindow(float[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ApplyWindow(data, GetWindow(data.Length, data.Length, data.Length));
    }

    public static void ApplyWindow(float[] data, float[] window)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(window);
        if (data.Length != window.Length)
        {
            throw new ArgumentException("Window length must match the target buffer length.", nameof(window));
        }

        int i = 0;
        
        if (Vector.IsHardwareAccelerated)
        {
            int vectorSize = Vector<float>.Count;
            for (; i <= data.Length - vectorSize; i += vectorSize)
            {
                var vData = new Vector<float>(data, i);
                var vWindow = new Vector<float>(window, i);
                (vData * vWindow).CopyTo(data, i);
            }
        }

        for (; i < data.Length; i++)
        {
            data[i] *= window[i];
        }
    }

    private static void ApplyWindow(float[] data, int length, float[] window)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(window);

        if (length < 0 || length > data.Length || length != window.Length)
        {
            throw new ArgumentException("Window length must match the logical target buffer length.", nameof(window));
        }

        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vectorSize = Vector<float>.Count;
            for (; i <= length - vectorSize; i += vectorSize)
            {
                var vData = new Vector<float>(data, i);
                var vWindow = new Vector<float>(window, i);
                (vData * vWindow).CopyTo(data, i);
            }
        }

        for (; i < length; i++)
        {
            data[i] *= window[i];
        }
    }

    public static float[] Mdct(float[] input)
    {
        int n2 = input.Length;
        int n = n2 / 2;
        float[] output = new float[n];
        Mdct(input, output);
        return output;
    }

    public static void Mdct(float[] input, float[] output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (input.Length != output.Length * 2 || (input.Length & 3) != 0)
        {
            throw new ArgumentException("MDCT input length must be exactly twice the output length and divisible by four.");
        }

        Dct4Plan plan = GetDct4Plan(output.Length);
        var workspace = new TransformWorkspace();
        workspace.EnsureTransformSize(plan.Size);
        workspace.EnsureFftSize(plan.FftSize);

        BuildMdctFoldedInput(input, 0, null, workspace.FoldedInput, input.Length);
        ComputeDct4(workspace.FoldedInput, output, plan, workspace);
    }

    public static float[] Imdct(float[] input)
    {
        int n = input.Length;
        int n2 = n * 2;
        float[] output = new float[n2];
        Imdct(input, output);
        return output;
    }

    public static void Imdct(float[] input, float[] output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (output.Length != input.Length * 2 || (output.Length & 3) != 0)
        {
            throw new ArgumentException("IMDCT output length must be exactly twice the input length and divisible by four.");
        }

        Dct4Plan plan = GetDct4Plan(input.Length);
        var workspace = new TransformWorkspace();
        workspace.EnsureTransformSize(plan.Size);
        workspace.EnsureFftSize(plan.FftSize);

        ComputeInverseDct4(input, workspace.FoldedOutput, plan, workspace);
        ExpandFoldedImdct(workspace.FoldedOutput, output);
    }

    private static void BuildMdctFoldedInput(float[] input, int inputOffset, float[]? window, float[] folded, int blockSize)
    {
        int halfSize = blockSize / 2;
        int quarterSize = halfSize / 2;

        for (int i = 0; i < quarterSize; i++)
        {
            int aIndex = inputOffset + i;
            int bIndex = inputOffset + halfSize - 1 - i;
            int cIndex = inputOffset + (halfSize + quarterSize - 1 - i);
            int dIndex = inputOffset + (halfSize + quarterSize + i);

            float a = input[aIndex];
            float b = input[bIndex];
            float c = input[cIndex];
            float d = input[dIndex];

            if (window is not null)
            {
                a *= window[i];
                b *= window[halfSize - 1 - i];
                c *= window[halfSize + quarterSize - 1 - i];
                d *= window[halfSize + quarterSize + i];
            }

            folded[i] = -(d + c);
            folded[quarterSize + i] = a - b;
        }
    }

    private static void ExpandFoldedImdct(float[] folded, float[] output)
    {
        ExpandFoldedImdct(folded, folded.Length, output);
    }

    private static void ExpandFoldedImdct(float[] folded, int transformSize, float[] output)
    {
        int halfSize = transformSize;
        int quarterSize = halfSize / 2;

        for (int i = 0; i < quarterSize; i++)
        {
            output[i] = folded[quarterSize + i];
            output[quarterSize + i] = -folded[halfSize - 1 - i];
            output[halfSize + i] = -folded[quarterSize - 1 - i];
            output[halfSize + quarterSize + i] = -folded[i];
        }
    }

    private static void OverlapAddMaterializedBlock(
        float[] block,
        int blockSize,
        float[] output,
        int outputOffset,
        float[] overlapBuffer,
        int leftOverlap,
        int flatEnd,
        int overlapCount)
    {
        int overlapEnd = Math.Min(leftOverlap, overlapCount);
        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int vSize = Vector<float>.Count;
            for (; i <= overlapEnd - vSize; i += vSize)
            {
                var b = new Vector<float>(block, i);
                var o = new Vector<float>(overlapBuffer, i);
                (b + o).CopyTo(output, outputOffset + i);
            }
        }
        for (; i < overlapEnd; i++)
        {
            output[outputOffset + i] = block[i] + overlapBuffer[i];
        }

        int copyLen = flatEnd - overlapEnd;
        if (copyLen > 0)
        {
            Buffer.BlockCopy(block, overlapEnd * sizeof(float), output, (outputOffset + overlapEnd) * sizeof(float), copyLen * sizeof(float));
        }

        int tailLen = blockSize - flatEnd;
        if (tailLen > 0)
        {
            Buffer.BlockCopy(block, flatEnd * sizeof(float), overlapBuffer, 0, tailLen * sizeof(float));
        }
    }

    private static void OverlapAddSynthesizedBlock(
        float[] folded,
        int transformSize,
        float[] window,
        float[] output,
        int outputOffset,
        float[] overlapBuffer,
        int leftOverlap,
        int flatEnd,
        int overlapCount)
    {
        int halfSize = transformSize;
        int quarterSize = halfSize / 2;
        int oi = 0;

        for (int i = 0; i < quarterSize; i++, oi++)
        {
            float sample = folded[quarterSize + i] * window[oi];
            if (oi < leftOverlap)
            {
                output[outputOffset + oi] = sample + ((oi < overlapCount) ? overlapBuffer[oi] : 0f);
            }
            else if (oi < flatEnd)
            {
                output[outputOffset + oi] = sample;
            }
            else
            {
                overlapBuffer[oi - flatEnd] = sample;
            }
        }

        for (int i = 0; i < quarterSize; i++, oi++)
        {
            float sample = -folded[halfSize - 1 - i] * window[oi];
            if (oi < leftOverlap)
            {
                output[outputOffset + oi] = sample + ((oi < overlapCount) ? overlapBuffer[oi] : 0f);
            }
            else if (oi < flatEnd)
            {
                output[outputOffset + oi] = sample;
            }
            else
            {
                overlapBuffer[oi - flatEnd] = sample;
            }
        }

        for (int i = 0; i < quarterSize; i++, oi++)
        {
            float sample = -folded[quarterSize - 1 - i] * window[oi];
            if (oi < leftOverlap)
            {
                output[outputOffset + oi] = sample + ((oi < overlapCount) ? overlapBuffer[oi] : 0f);
            }
            else if (oi < flatEnd)
            {
                output[outputOffset + oi] = sample;
            }
            else
            {
                overlapBuffer[oi - flatEnd] = sample;
            }
        }

        for (int i = 0; i < quarterSize; i++, oi++)
        {
            float sample = -folded[i] * window[oi];
            if (oi < leftOverlap)
            {
                output[outputOffset + oi] = sample + ((oi < overlapCount) ? overlapBuffer[oi] : 0f);
            }
            else if (oi < flatEnd)
            {
                output[outputOffset + oi] = sample;
            }
            else
            {
                overlapBuffer[oi - flatEnd] = sample;
            }
        }
    }

    private static void ComputeDct4(float[] input, float[] output, Dct4Plan plan, TransformWorkspace workspace)
    {
        workspace.EnsureFftSize(plan.FftSize);
        double[] real = workspace.FftReal;
        double[] imag = workspace.FftImag;

        int tail = plan.FftSize - plan.Size;
        if (tail > 0)
        {
            Array.Clear(real, plan.Size, tail);
            Array.Clear(imag, plan.Size, tail);
        }

        for (int i = 0; i < plan.Size; i++)
        {
            double value = input[i];
            real[i] = value * plan.PreCos[i];
            imag[i] = -value * plan.PreSin[i];
        }

        ComputeComplexFft(real, imag, plan.FftPlan, inverse: false);

        for (int i = 0; i < plan.FftSize; i++)
        {
            double fftReal = real[i];
            double fftImag = imag[i];
            double kernelReal = plan.KernelFftReal[i];
            double kernelImag = plan.KernelFftImag[i];
            real[i] = (fftReal * kernelReal) - (fftImag * kernelImag);
            imag[i] = (fftReal * kernelImag) + (fftImag * kernelReal);
        }

        ComputeComplexFft(real, imag, plan.FftPlan, inverse: true);

        for (int i = 0; i < plan.Size; i++)
        {
            output[i] = (float)((real[i] * plan.PreCos[i]) + (imag[i] * plan.PreSin[i]));
        }
    }

    private static void ComputeInverseDct4(float[] input, float[] output, Dct4Plan plan, TransformWorkspace workspace)
    {
        ComputeDct4(input, output, plan, workspace);

        float scale = 2.0f / plan.Size;
        for (int i = 0; i < plan.Size; i++)
        {
            output[i] *= scale;
        }
    }

    private static ComplexFftPlan GetComplexFftPlan(int size)
    {
        return ComplexFftPlanCache.GetOrAdd(size, fftSize =>
        {
            int levels = 31 - BitOperations.LeadingZeroCount((uint)fftSize);
            int[] bitReverse = new int[fftSize];
            for (int i = 0; i < fftSize; i++)
            {
                bitReverse[i] = ReverseBits(i, levels);
            }

            int halfFft = fftSize / 2;
            double[] twiddleReal = new double[Math.Max(1, halfFft)];
            double[] twiddleImag = new double[Math.Max(1, halfFft)];
            for (int k = 0; k < halfFft; k++)
            {
                double theta = -2.0 * Math.PI * k / fftSize;
                twiddleReal[k] = Math.Cos(theta);
                twiddleImag[k] = Math.Sin(theta);
            }

            return new ComplexFftPlan
            {
                Size = fftSize,
                BitReverse = bitReverse,
                TwiddleReal = twiddleReal,
                TwiddleImag = twiddleImag,
            };
        });
    }

    private static Dct4Plan GetDct4Plan(int size)
    {
        return Dct4PlanCache.GetOrAdd(size, dctSize =>
        {
            int fftSize = 1;
            while (fftSize < (dctSize * 2) - 1)
            {
                fftSize <<= 1;
            }

            double[] preCos = new double[dctSize];
            double[] preSin = new double[dctSize];
            for (int i = 0; i < dctSize; i++)
            {
                double angle = Math.PI * (i + 0.5) * (i + 0.5) / (2.0 * dctSize);
                preCos[i] = Math.Cos(angle);
                preSin[i] = Math.Sin(angle);
            }

            double[] kernelFftReal = new double[fftSize];
            double[] kernelFftImag = new double[fftSize];
            kernelFftReal[0] = 1.0;

            for (int i = 1; i < dctSize; i++)
            {
                double angle = Math.PI * i * i / (2.0 * dctSize);
                double real = Math.Cos(angle);
                double imag = Math.Sin(angle);
                kernelFftReal[i] = real;
                kernelFftImag[i] = imag;
                kernelFftReal[fftSize - i] = real;
                kernelFftImag[fftSize - i] = imag;
            }

            ComplexFftPlan fftPlan = GetComplexFftPlan(fftSize);
            ComputeComplexFft(kernelFftReal, kernelFftImag, fftPlan, inverse: false);

            return new Dct4Plan
            {
                Size = dctSize,
                FftSize = fftSize,
                FftPlan = fftPlan,
                PreCos = preCos,
                PreSin = preSin,
                KernelFftReal = kernelFftReal,
                KernelFftImag = kernelFftImag,
            };
        });
    }

    private static void ComputeComplexFft(double[] real, double[] imag, ComplexFftPlan plan, bool inverse)
    {
        int[] bitReverse = plan.BitReverse;
        for (int i = 0; i < plan.Size; i++)
        {
            int j = bitReverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        double[] twiddleReal = plan.TwiddleReal;
        double[] twiddleImag = plan.TwiddleImag;
        double imagSign = inverse ? -1.0 : 1.0;

        for (int size = 2; size <= plan.Size; size <<= 1)
        {
            int halfSize = size >> 1;
            int stride = plan.Size / size;

            for (int start = 0; start < plan.Size; start += size)
            {
                for (int k = 0; k < halfSize; k++)
                {
                    int evenIndex = start + k;
                    int oddIndex = evenIndex + halfSize;
                    int twiddleIndex = k * stride;
                    double wReal = twiddleReal[twiddleIndex];
                    double wImag = twiddleImag[twiddleIndex] * imagSign;

                    double oddReal = (real[oddIndex] * wReal) - (imag[oddIndex] * wImag);
                    double oddImag = (real[oddIndex] * wImag) + (imag[oddIndex] * wReal);

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imag[oddIndex] = imag[evenIndex] - oddImag;
                    real[evenIndex] += oddReal;
                    imag[evenIndex] += oddImag;
                }
            }
        }

        if (!inverse)
        {
            return;
        }

        double scale = 1.0 / plan.Size;
        for (int i = 0; i < plan.Size; i++)
        {
            real[i] *= scale;
            imag[i] *= scale;
        }
    }

    private static int ReverseBits(int value, int bitCount)
    {
        int result = 0;
        for (int i = 0; i < bitCount; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}