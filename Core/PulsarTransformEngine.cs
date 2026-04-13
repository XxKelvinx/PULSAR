using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Numerics;

public static class PulsarTransformEngine
{
    private static readonly ConcurrentDictionary<(int size, int prevSize, int nextSize), float[]> WindowCache = new();
    private static readonly ConcurrentDictionary<int, float[]> MdctKernelCache = new();
    private static readonly ConcurrentDictionary<int, float[]> ImdctKernelCache = new();

    static PulsarTransformEngine()
    {
        PulsarBlockLadder.Validate();
    }

    public static float[] Process(float[] input, PulsarPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var framePlans = planner.PlanSong(input, null);
        return RenderSequentialPath(input, framePlans);
    }

    public static (float[] Output, List<PulsarFramePlan> Plans) ProcessWithPlans(float[] input, PulsarPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var framePlans = planner.PlanSong(input, null);
        return (RenderSequentialPath(input, framePlans), framePlans);
    }

    public static float[] ProcessLegacyPlanner(float[] input, PulsarPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var framePlans = planner.PlanSong(input, null);
        return RenderSequentialPath(input, framePlans);
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

        // Für Legacy erstellen wir einfach einen Dummy-Plan, der stur auf der BlockSize bleibt
        int segmentCount = (input.Length + PulsarBlockLadder.ControlHopSize - 1) / PulsarBlockLadder.ControlHopSize;
        var dummyPlans = new List<PulsarFramePlan>(segmentCount);
        for(int i=0; i<segmentCount; i++)
        {
            dummyPlans.Add(new PulsarFramePlan {
                 SegmentIndex = i, BlockSize = blockSize, PreviousBlockSize = blockSize, NextBlockSize = blockSize,
                 TargetBlockSize = blockSize, Direction = PulsarSwitchDirection.Hold,
                 TransientLevel = PulsarTransientLevel.None, AttackRatio = 0, PeakDeltaDb = 0,
                 AttackIndex = 0, EnergyModulation = 0, CrestFactor = 0, LowBandRatio = 0,
                 HighBandRatio = 0, SustainedHighBandRatio = 0, DesiredLadderPosition = 0,
                 ClueStrength = 0, PathCost = 0, Spectral = new SpectralProfile(), PreEchoRisk = 0, SpectralFlux = 0
            });
        }

        return RenderSequentialPath(input, dummyPlans);
    }

    // --- DIE NEUE SEQUENZIELLE MASTER-SCHLEIFE ---
    private static float[] RenderSequentialPath(float[] input, IReadOnlyList<PulsarFramePlan> framePlans)
    {
        if (framePlans.Count == 0 || input.Length == 0) return Array.Empty<float>();

        int initialBlockSize = framePlans[0].BlockSize;
        int initialHop = initialBlockSize / 2;

        float[] paddedInput = new float[input.Length + initialHop * 2];
        Array.Copy(input, 0, paddedInput, initialHop, input.Length);

        float[] output = new float[paddedInput.Length + PulsarBlockLadder.MaxBlockSize];
        float[] overlapBuffer = new float[PulsarBlockLadder.MaxBlockSize];

        int currentInputPos = 0;
        int currentOutputPos = 0;
        int previousBlockSize = initialBlockSize;

        while (currentInputPos < paddedInput.Length - PulsarBlockLadder.MinBlockSize)
        {
            int unpaddedPos = currentInputPos - initialHop;
            int segmentIndex = unpaddedPos >= 0 ? (unpaddedPos / PulsarBlockLadder.ControlHopSize) : 0;
            segmentIndex = Math.Clamp(segmentIndex, 0, framePlans.Count - 1);

            int currentBlockSize = framePlans[segmentIndex].BlockSize;

            if (currentInputPos + currentBlockSize > paddedInput.Length)
            {
                currentBlockSize = PulsarBlockLadder.MinBlockSize;
                while (currentInputPos + currentBlockSize > paddedInput.Length && currentBlockSize > 2)
                    currentBlockSize /= 2;
                if (currentBlockSize < PulsarBlockLadder.MinBlockSize) break;
            }

            int nextUnpaddedPos = unpaddedPos + (currentBlockSize / 2);
            int nextSegmentIndex = nextUnpaddedPos >= 0 ? (nextUnpaddedPos / PulsarBlockLadder.ControlHopSize) : 0;
            nextSegmentIndex = Math.Clamp(nextSegmentIndex, 0, framePlans.Count - 1);
            int nextBlockSize = framePlans[nextSegmentIndex].BlockSize;

            int leftOverlap = Math.Clamp(previousBlockSize / 2, 1, currentBlockSize - 1);
            int rightOverlap = Math.Clamp(nextBlockSize / 2, 1, currentBlockSize - leftOverlap);
            int flatEnd = currentBlockSize - rightOverlap;

            float[] frame = new float[currentBlockSize];
            int available = Math.Min(currentBlockSize, paddedInput.Length - currentInputPos);
            Array.Copy(paddedInput, currentInputPos, frame, 0, available);

            float[] window = GetWindow(currentBlockSize, previousBlockSize, nextBlockSize);
            ApplyWindow(frame, window);

            float[] spectrum = new float[currentBlockSize / 2];
            float[] reconstructed = new float[currentBlockSize];
            Mdct(frame, spectrum);
            Imdct(spectrum, reconstructed);
            ApplyWindow(reconstructed, window);

            for (int i = 0; i < currentBlockSize; i++)
            {
                int outIndex = currentOutputPos + i;
                if (i < leftOverlap)
                {
                    output[outIndex] = reconstructed[i] + overlapBuffer[i];
                }
                else if (i < flatEnd)
                {
                    output[outIndex] = reconstructed[i];
                }
                else
                {
                    overlapBuffer[i - flatEnd] = reconstructed[i];
                }
            }

            int hopSize = flatEnd;
            currentInputPos += hopSize;
            currentOutputPos += hopSize;
            previousBlockSize = currentBlockSize;
        }

        float[] trimmed = new float[input.Length];
        int copyLength = Math.Min(input.Length, output.Length - initialHop);
        if (copyLength > 0)
            Array.Copy(output, initialHop, trimmed, 0, copyLength);

        return trimmed;
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

        float[] paddedInput = new float[input.Length + initialHop * 2];
        Array.Copy(input, 0, paddedInput, initialHop, input.Length);

        float[] output = new float[paddedInput.Length + PulsarBlockLadder.MaxBlockSize];
        float[] overlapBuffer = new float[PulsarBlockLadder.MaxBlockSize];

        int currentInputPos = 0;
        int currentOutputPos = 0;
        int previousBlockSize = initialBlockSize;

        while (currentInputPos < paddedInput.Length - PulsarBlockLadder.MinBlockSize)
        {
            int unpaddedPos = currentInputPos - initialHop;
            int segmentIndex = unpaddedPos >= 0 ? (unpaddedPos / PulsarBlockLadder.ControlHopSize) : 0;
            segmentIndex = Math.Clamp(segmentIndex, 0, framePlans.Count - 1);

            int currentBlockSize = framePlans[segmentIndex].BlockSize;

            if (currentInputPos + currentBlockSize > paddedInput.Length)
            {
                currentBlockSize = PulsarBlockLadder.MinBlockSize;
                while (currentInputPos + currentBlockSize > paddedInput.Length && currentBlockSize > 2)
                    currentBlockSize /= 2;
                if (currentBlockSize < PulsarBlockLadder.MinBlockSize) break;
            }

            int nextUnpaddedPos = unpaddedPos + (currentBlockSize / 2);
            int nextSegmentIndex = nextUnpaddedPos >= 0 ? (nextUnpaddedPos / PulsarBlockLadder.ControlHopSize) : 0;
            nextSegmentIndex = Math.Clamp(nextSegmentIndex, 0, framePlans.Count - 1);
            int nextBlockSize = framePlans[nextSegmentIndex].BlockSize;

            int leftOverlap = Math.Clamp(previousBlockSize / 2, 1, currentBlockSize - 1);
            int rightOverlap = Math.Clamp(nextBlockSize / 2, 1, currentBlockSize - leftOverlap);
            int flatEnd = currentBlockSize - rightOverlap;

            float[] frame = new float[currentBlockSize];
            int available = Math.Min(currentBlockSize, paddedInput.Length - currentInputPos);
            Array.Copy(paddedInput, currentInputPos, frame, 0, available);

            float[] window = GetWindow(currentBlockSize, previousBlockSize, nextBlockSize);
            ApplyWindow(frame, window);

            float[] spectrum = new float[currentBlockSize / 2];
            float[] reconstructed = new float[currentBlockSize];
            Mdct(frame, spectrum);

            var quantContext = BuildQuantizationContext(Math.Max(0, unpaddedPos), currentBlockSize, allocations, psychoFrames);
            PulsarQuantizer.QuantizeSpectrum(spectrum, quantContext.BandBits, quantContext.Psycho);

            Imdct(spectrum, reconstructed);
            ApplyWindow(reconstructed, window);

            for (int i = 0; i < currentBlockSize; i++)
            {
                int outIndex = currentOutputPos + i;
                if (i < leftOverlap)
                {
                    output[outIndex] = reconstructed[i] + overlapBuffer[i];
                }
                else if (i < flatEnd)
                {
                    output[outIndex] = reconstructed[i];
                }
                else
                {
                    overlapBuffer[i - flatEnd] = reconstructed[i];
                }
            }

            int hopSize = flatEnd;
            currentInputPos += hopSize;
            currentOutputPos += hopSize;
            previousBlockSize = currentBlockSize;
        }

        float[] trimmed = new float[input.Length];
        int copyLength = Math.Min(input.Length, output.Length - initialHop);
        if (copyLength > 0)
            Array.Copy(output, initialHop, trimmed, 0, copyLength);

        return trimmed;
    }

    private static (int[] BandBits, Pulsar.Psycho.PulsarPsychoResult Psycho) BuildQuantizationContext(
        int offset,
        int blockSize,
        IReadOnlyList<PulsarFrameAllocation> allocations,
        IReadOnlyList<Pulsar.Psycho.PulsarPsychoResult> psychoFrames)
    {
        int controlHop = PulsarBlockLadder.ControlHopSize;
        int startSegment = Math.Clamp(offset / controlHop, 0, allocations.Count - 1);
        int endSegment = Math.Clamp((int)Math.Ceiling((offset + blockSize) / (double)controlHop) - 1, startSegment, allocations.Count - 1);
        int centerSegment = Math.Clamp(((offset + (blockSize / 2)) / controlHop), 0, psychoFrames.Count - 1);
        int bandCount = allocations[centerSegment].BandBits.Length;
        int[] averagedBandBits = new int[bandCount];

        for (int segmentIndex = startSegment; segmentIndex <= endSegment; segmentIndex++)
        {
            int[] sourceBits = allocations[segmentIndex].BandBits;
            for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
            {
                averagedBandBits[bandIndex] += sourceBits[Math.Min(bandIndex, sourceBits.Length - 1)];
            }
        }

        return (averagedBandBits, psychoFrames[centerSegment]);
    }

    // --- ASYMMETRISCHE FENSTER GENERIERUNG (SIMD-Optimiert) ---
    private static float[] BuildAsymmetricWindow(int size, int prevSize, int nextSize)
    {
        int leftOverlap = Math.Clamp(prevSize / 2, 1, size - 1);
        int rightOverlap = Math.Clamp(nextSize / 2, 1, size - leftOverlap);
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
            float[] temp = new float[vectorSize];

            int i = 0;
            for (; i <= leftOverlap - vectorSize && i < size; i += vectorSize)
            {
                var baseIndex = new Vector<float>(i);
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
                var baseIndex = new Vector<float>(i);
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
        int n2 = input.Length;
        int n = n2 / 2;
        float[] kernel = GetMdctKernel(n2);

        Parallel.For(0, n, k =>
        {
            double sum = 0.0;
            int kernelOffset = k * n2;
            int i = 0;

            if (Vector.IsHardwareAccelerated)
            {
                int vectorSize = Vector<float>.Count;
                Vector<float> vSum = Vector<float>.Zero;
                
                for (; i <= n2 - vectorSize; i += vectorSize)
                {
                    var vInput = new Vector<float>(input, i);
                    var vKernel = new Vector<float>(kernel, kernelOffset + i);
                    vSum += vInput * vKernel; 
                }
                
                for (int v = 0; v < vectorSize; v++)
                {
                    sum += vSum[v];
                }
            }

            for (; i < n2; i++)
            {
                sum += input[i] * kernel[kernelOffset + i];
            }

            output[k] = (float)sum;
        });
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
        int n = input.Length;
        int n2 = n * 2;
        float[] kernel = GetImdctKernel(n2);

        Parallel.For(0, n2, i =>
        {
            double sum = 0.0;
            int kernelOffset = i * n;
            int k = 0;

            if (Vector.IsHardwareAccelerated)
            {
                int vectorSize = Vector<float>.Count;
                Vector<float> vSum = Vector<float>.Zero;
                
                for (; k <= n - vectorSize; k += vectorSize)
                {
                    var vInput = new Vector<float>(input, k);
                    var vKernel = new Vector<float>(kernel, kernelOffset + k);
                    vSum += vInput * vKernel;
                }
                
                for (int v = 0; v < vectorSize; v++)
                {
                    sum += vSum[v];
                }
            }

            for (; k < n; k++)
            {
                sum += input[k] * kernel[kernelOffset + k];
            }

            output[i] = (float)(sum * 2.0 / n);
        });
    }

    private static float[] GetMdctKernel(int n2)
    {
        return MdctKernelCache.GetOrAdd(n2, BuildMdctKernel);
    }

    private static float[] GetImdctKernel(int n2)
    {
        return ImdctKernelCache.GetOrAdd(n2, BuildImdctKernel);
    }

    private static float[] BuildMdctKernel(int n2)
    {
        int n = n2 / 2;
        double constant = Math.PI / n;
        float[] kernel = new float[n * n2];

        for (int k = 0; k < n; k++)
        {
            int offset = k * n2;
            for (int i = 0; i < n2; i++)
            {
                kernel[offset + i] = (float)Math.Cos(constant * (i + 0.5 + n / 2.0) * (k + 0.5));
            }
        }

        return kernel;
    }

    private static float[] BuildImdctKernel(int n2)
    {
        int n = n2 / 2;
        double constant = Math.PI / n;
        float[] kernel = new float[n2 * n];

        for (int i = 0; i < n2; i++)
        {
            int offset = i * n;
            for (int k = 0; k < n; k++)
            {
                kernel[offset + k] = (float)Math.Cos(constant * (i + 0.5 + n / 2.0) * (k + 0.5));
            }
        }

        return kernel;
    }
}