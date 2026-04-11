using NAudio.Wave;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

var workspaceRoot = FindWorkspaceRoot();
var defaultInputPath = Path.Combine(workspaceRoot, "TestWAVs", "Strike A Pose! 30s.wav");
var defaultOutputPath = Path.Combine(workspaceRoot, "TestWAVs", "Output", "Strike A Pose! 30s pulsar-process.wav");

if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
{
    PrintUsage();
    return;
}

if (args.Length == 3 && args[0] == "--compare")
{
    CompareWavs(args[1], args[2]);
    return;
}

bool useVbrMode = args.Length >= 4 && args[0] == "--vbr";
bool useLegacyMode = args.Length >= 3 && args[0] == "--legacy";
bool useLegacyPlannerMode = args.Length >= 3 && args[0] == "--legacyP";
bool useLegacyPlannerFastMode = args.Length >= 3 && args[0] == "--legacyP-fast";
int legacyBlockSize = PulsarBlockLadder.DefaultBlockSize;
int targetKbps = 128;
string inputPath;
string outputPath;

if (useVbrMode)
{
	if (!int.TryParse(args[1], out targetKbps) || targetKbps < 16 || targetKbps > 480)
	{
		Console.Error.WriteLine("Invalid VBR target bitrate. Use a number between 16 and 480.");
		Environment.Exit(1);
		return;
	}

	inputPath = Path.GetFullPath(args[2]);
	outputPath = Path.GetFullPath(args[3]);
}
else if (useLegacyMode)
{
    inputPath = Path.GetFullPath(args[1]);
    outputPath = Path.GetFullPath(args[2]);

    if (args.Length > 3)
    {
        if (!int.TryParse(args[3], out legacyBlockSize) || !PulsarBlockLadder.IsValidBlockSize(legacyBlockSize))
        {
            Console.Error.WriteLine("Invalid legacy block size. Use one of: " + string.Join(", ", PulsarBlockLadder.Steps));
            Environment.Exit(1);
            return;
        }
    }
}
else if (useLegacyPlannerMode || useLegacyPlannerFastMode)
{
    inputPath = Path.GetFullPath(args[1]);
    outputPath = Path.GetFullPath(args[2]);
}
else
{
    inputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : defaultInputPath;
    outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : defaultOutputPath;
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    Environment.Exit(1);
    return;
}

PrepareOutputFolder(outputPath);
outputPath = ResolveWritableOutputPath(outputPath);

using var reader = new AudioFileReader(inputPath);
int channels = reader.WaveFormat.Channels;
int sampleRate = reader.WaveFormat.SampleRate;

var samples = ReadAllSamples(reader);
object ProgressLock = new();
float[] processed;
PulsarPlanner[]? planners = null;
List<PulsarFrameAllocation>[]? allocationsByChannel = null;

if (useLegacyMode)
{
	processed = ProcessLegacy(samples, channels, legacyBlockSize);
}
else if (useLegacyPlannerMode || useLegacyPlannerFastMode)
{
	(processed, planners) = ProcessLegacyPlannerWithTimeout(
	    samples,
	    channels,
	    TimeSpan.FromSeconds(180),
	    useLegacyPlannerFastMode);
}
else
{
	(processed, planners, allocationsByChannel) = ProcessWithPulsar(samples, channels, sampleRate, targetKbps);
}

var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
using (var writer = new WaveFileWriter(outputPath, outputFormat))
{
    writer.WriteSamples(processed, 0, processed.Length);
}

if (!useLegacyMode && planners is not null)
{
	WritePlannerLogs(outputPath, sampleRate, planners);
	if (allocationsByChannel is not null)
	{
		WriteAllocationLogs(outputPath, sampleRate, allocationsByChannel, targetKbps);
	}
	WriteResidualComparison(inputPath, outputPath);
}

Console.WriteLine(useLegacyMode
    ? $"Legacy direct render complete: {outputPath} (blockSize={legacyBlockSize})"
    : useLegacyPlannerFastMode
        ? $"Legacy planner-only fast render complete: {outputPath}"
        : useLegacyPlannerMode
            ? $"Legacy planner-only render complete: {outputPath}"
            : $"Pulsar process render complete: {outputPath}");

static void PrepareOutputFolder(string outputPath)
{
    string outputDirectory = Path.GetDirectoryName(outputPath)!;
    string oldDirectory = Path.Combine(outputDirectory, "Old");

    Directory.CreateDirectory(outputDirectory);
    Directory.CreateDirectory(oldDirectory);

    foreach (string wavPath in Directory.GetFiles(outputDirectory, "*.wav", SearchOption.TopDirectoryOnly))
    {
        string fileName = Path.GetFileName(wavPath);
        string archivedPath = Path.Combine(oldDirectory, fileName);

        if (File.Exists(archivedPath))
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            archivedPath = Path.Combine(oldDirectory, $"{baseName}-{timestamp}{extension}");
        }

        try
        {
            File.Move(wavPath, archivedPath);
        }
        catch (IOException)
        {
            // Skip files that are currently open, so rendering can continue.
        }
        catch (UnauthorizedAccessException)
        {
            // Skip files that are currently open, so rendering can continue.
        }
    }
}

static string ResolveWritableOutputPath(string outputPath)
{
    if (!File.Exists(outputPath))
    {
        return outputPath;
    }

    try
    {
        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return outputPath;
    }
    catch (IOException)
    {
        return BuildTimestampedPath(outputPath);
    }
    catch (UnauthorizedAccessException)
    {
        return BuildTimestampedPath(outputPath);
    }
}

static string BuildTimestampedPath(string outputPath)
{
    string directory = Path.GetDirectoryName(outputPath)!;
    string baseName = Path.GetFileNameWithoutExtension(outputPath);
    string extension = Path.GetExtension(outputPath);
    string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    return Path.Combine(directory, $"{baseName}-{timestamp}{extension}");
}

static float[] ReadAllSamples(AudioFileReader reader)
{
    var allSamples = new List<float>();
    var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];

    int read;
    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        for (int i = 0; i < read; i++)
        {
            allSamples.Add(buffer[i]);
        }
    }

    return allSamples.ToArray();
}

static (float[] Processed, PulsarPlanner[] Planners, List<PulsarFrameAllocation>[] Allocations) ProcessWithPulsar(float[] interleavedSamples, int channels, int sampleRate, int targetKbps)
{
	var channelBuffers = new float[channels][];
	var planners = new PulsarPlanner[channels];
	int frames = interleavedSamples.Length / channels;

	for (int channel = 0; channel < channels; channel++)
	{
		channelBuffers[channel] = new float[frames];
		planners[channel] = new PulsarPlanner();
	}

	for (int frame = 0; frame < frames; frame++)
	{
		for (int channel = 0; channel < channels; channel++)
		{
			channelBuffers[channel][frame] = interleavedSamples[frame * channels + channel];
		}
	}

	var processedChannels = new float[channels][];
	var allocations = new List<PulsarFrameAllocation>[channels];
	var allocator = new PulsarAllocator(new PulsarAllocationConfig
	{
		TargetKbps = targetKbps,
		SampleRate = sampleRate,
		HopSize = PulsarBlockLadder.ControlHopSize,
	});

	Parallel.For(0, channels, channel =>
	{
		var result = PulsarTransformEngine.ProcessWithPlans(channelBuffers[channel], planners[channel]);
		processedChannels[channel] = result.Output;
		allocations[channel] = allocator.AllocateSong(result.Plans);
	});

	var output = new float[interleavedSamples.Length];
	for (int channel = 0; channel < channels; channel++)
	{
		float[] processedChannel = processedChannels[channel];

		for (int frame = 0; frame < frames; frame++)
		{
			output[frame * channels + channel] = processedChannel[frame];
		}
	}

	return (output, planners, allocations);
}

static float[] ProcessLegacy(float[] interleavedSamples, int channels, int blockSize)
{
    var channelBuffers = new float[channels][];
    int frames = interleavedSamples.Length / channels;

    for (int channel = 0; channel < channels; channel++)
    {
        channelBuffers[channel] = new float[frames];
    }

    for (int frame = 0; frame < frames; frame++)
    {
        for (int channel = 0; channel < channels; channel++)
        {
            channelBuffers[channel][frame] = interleavedSamples[frame * channels + channel];
        }
    }

    var processedChannels = new float[channels][];
    Parallel.For(0, channels, channel =>
    {
        processedChannels[channel] = PulsarTransformEngine.ProcessLegacy(channelBuffers[channel], blockSize);
    });

    var output = new float[interleavedSamples.Length];
    for (int channel = 0; channel < channels; channel++)
    {
        float[] processedChannel = processedChannels[channel];

        for (int frame = 0; frame < frames; frame++)
        {
            output[frame * channels + channel] = processedChannel[frame];
        }
    }

    return output;
}

void ReportProgress(int channel, int completedSegments, int totalSegments)
{
    lock (ProgressLock)
    {
        int barWidth = 40;
        int filled = totalSegments > 0
            ? (int)Math.Round((double)completedSegments / totalSegments * barWidth)
            : 0;
        string bar = new string('#', Math.Min(barWidth, Math.Max(0, filled))).PadRight(barWidth, '-');
        double percent = totalSegments > 0 ? (double)completedSegments / totalSegments * 100.0 : 0.0;
        Console.WriteLine($"Channel {channel}: [{bar}] {percent:0.0}% ({completedSegments}/{totalSegments})");
    }
}

(float[] Processed, PulsarPlanner[] Planners) ProcessLegacyPlannerWithTimeout(float[] interleavedSamples, int channels, TimeSpan timeout, bool fastMode)
{
    Console.WriteLine($"Starting legacy planner render with {timeout.TotalSeconds:0} second timeout...{(fastMode ? " (fast mode)" : string.Empty)}");
    var stopwatch = Stopwatch.StartNew();
    var processingTask = Task.Run(() =>
    {
        PulsarPlanner[] localPlanners = Array.Empty<PulsarPlanner>();
        float[] result = ProcessLegacyPlanner(interleavedSamples, channels, out localPlanners, fastMode);
        return (Result: result, Planners: localPlanners);
    });

    if (!processingTask.Wait(timeout))
    {
        stopwatch.Stop();
        Console.Error.WriteLine($"Legacy planner render did not complete within {timeout.TotalSeconds:0} seconds and will be aborted.");
        Environment.Exit(1);
    }

    stopwatch.Stop();
    Console.WriteLine($"Legacy planner render completed in {stopwatch.Elapsed:mm\\:ss\\.fff}.");
    return (processingTask.Result.Result, processingTask.Result.Planners);
}

float[] ProcessLegacyPlanner(float[] interleavedSamples, int channels, out PulsarPlanner[] planners, bool fastMode)
{
    var channelBuffers = new float[channels][];
    planners = new PulsarPlanner[channels];
    int frames = interleavedSamples.Length / channels;

    for (int channel = 0; channel < channels; channel++)
    {
        channelBuffers[channel] = new float[frames];
        planners[channel] = fastMode
            ? new PulsarPlanner(new PulsarPlannerSettings { SpectralFftSize = 512, UseFastAnalysis = true })
            : new PulsarPlanner();
    }

    for (int frame = 0; frame < frames; frame++)
    {
        for (int channel = 0; channel < channels; channel++)
        {
            channelBuffers[channel][frame] = interleavedSamples[frame * channels + channel];
        }
    }

    var processedChannels = new float[channels][];
    var plannerCache = planners;
    Parallel.For(0, channels, channel =>
    {
        processedChannels[channel] = PulsarTransformEngine.ProcessLegacyPlanner(
            channelBuffers[channel],
            plannerCache[channel],
            (index, total) => ReportProgress(channel, index, total));
    });

    var output = new float[interleavedSamples.Length];
    for (int channel = 0; channel < channels; channel++)
    {
        float[] processedChannel = processedChannels[channel];

        for (int frame = 0; frame < frames; frame++)
        {
            output[frame * channels + channel] = processedChannel[frame];
        }
    }

    return output;
}

static void WritePlannerLogs(string outputPath, int sampleRate, IReadOnlyList<PulsarPlanner> planners)
{
    string logPath = Path.Combine(
        Path.GetDirectoryName(outputPath)!,
        $"{Path.GetFileNameWithoutExtension(outputPath)}.planner.log.txt");

    using var writer = new StreamWriter(logPath, false);
    writer.WriteLine($"Pulsar planner log for {Path.GetFileName(outputPath)}");
    writer.WriteLine($"SampleRate={sampleRate}");
    writer.WriteLine($"ControlHopSize={PulsarBlockLadder.ControlHopSize}");
    writer.WriteLine($"SegmentDurationSeconds={FormatDouble((double)PulsarBlockLadder.ControlHopSize / sampleRate)}");

    for (int channel = 0; channel < planners.Count; channel++)
    {
        writer.WriteLine();
        writer.WriteLine($"[Channel {channel}]");
        writer.WriteLine("StartSec\tSegment\tStartSample\tPrevBlock\tBlock\tTarget\tDirection\tTransient\tAttackRatio\tPeakDeltaDb\tAttackIndex\tEnergyMod\tCrest\tLowBand\tHiBand\tHiBandSmooth\tDesiredPos\tClue\tPathCost");

        foreach (PulsarFramePlan plan in planners[channel].LastPlan)
        {
            int startSample = plan.SegmentIndex * PulsarBlockLadder.ControlHopSize;
            double startSeconds = (double)startSample / sampleRate;
            writer.WriteLine(string.Join('\t',
                FormatDouble(startSeconds),
                plan.SegmentIndex,
                startSample,
                plan.PreviousBlockSize,
                plan.BlockSize,
                plan.TargetBlockSize,
                plan.Direction,
                plan.TransientLevel,
                FormatDouble(plan.AttackRatio),
                FormatDouble(plan.PeakDeltaDb),
                plan.AttackIndex,
                FormatDouble(plan.EnergyModulation),
                FormatDouble(plan.CrestFactor),
                FormatDouble(plan.LowBandRatio),
                FormatDouble(plan.HighBandRatio),
                FormatDouble(plan.SustainedHighBandRatio),
                FormatDouble(plan.DesiredLadderPosition),
                FormatDouble(plan.ClueStrength),
                FormatDouble(plan.PathCost)));
        }
    }
    Console.WriteLine($"Planner log written: {logPath}");
}

static void WriteAllocationLogs(string outputPath, int sampleRate, IReadOnlyList<List<PulsarFrameAllocation>> allocationsByChannel, int targetKbps)
{
    string logPath = Path.Combine(
        Path.GetDirectoryName(outputPath)!,
        $"{Path.GetFileNameWithoutExtension(outputPath)}.allocation.log.txt");

    using var writer = new StreamWriter(logPath, false);
    writer.WriteLine($"Pulsar allocation log for {Path.GetFileName(outputPath)}");
    writer.WriteLine($"SampleRate={sampleRate}");
    writer.WriteLine($"TargetKbps={targetKbps}");
    writer.WriteLine($"ControlHopSize={PulsarBlockLadder.ControlHopSize}");
    writer.WriteLine($"SegmentDurationSeconds={FormatDouble((double)PulsarBlockLadder.ControlHopSize / sampleRate)}");

    for (int channel = 0; channel < allocationsByChannel.Count; channel++)
    {
        writer.WriteLine();
        writer.WriteLine($"[Channel {channel}]");
        writer.WriteLine("StartSec\tSegment\tTargetBits\tMetadataBits\tBlockBits\tBandCount\tComplexity\tBandBits[0..8]");

        var allocations = allocationsByChannel[channel];
        for (int segmentIndex = 0; segmentIndex < allocations.Count; segmentIndex++)
        {
            PulsarFrameAllocation allocation = allocations[segmentIndex];
            int startSample = segmentIndex * PulsarBlockLadder.ControlHopSize;
            double startSeconds = (double)startSample / sampleRate;
            writer.WriteLine(string.Join('\t',
                FormatDouble(startSeconds),
                segmentIndex,
                allocation.TargetBits,
                allocation.MetadataBits,
                allocation.BlockBits,
                allocation.BandBits.Length,
                FormatDouble(allocation.ComplexityWeight),
                FormatBandBits(allocation.BandBits)));
        }
    }
}

static string FormatBandBits(int[] bandBits)
{
    if (bandBits.Length == 0)
    {
        return string.Empty;
    }

    int maxPreview = Math.Min(8, bandBits.Length);
    var preview = new string[maxPreview];
    for (int i = 0; i < maxPreview; i++)
    {
        preview[i] = bandBits[i].ToString();
    }

    string summary = string.Join(",", preview);
    if (bandBits.Length > maxPreview)
    {
        summary += ",...";
    }

    return summary;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --vbr <kbps> <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacy <input.wav> <output.wav> [blockSize]");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacyP <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacyP-fast <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --compare <original.wav> <processed.wav>");
    Console.WriteLine();
    Console.WriteLine("Legacy mode renders a fixed stationary path without using the planner.");
    Console.WriteLine("LegacyP mode renders using the planner decisions, but without planner switching blending.");
    Console.WriteLine("Valid block sizes: " + string.Join(", ", PulsarBlockLadder.Steps));
}

static void WriteResidualComparison(string originalPath, string processedPath)
{
    if (!File.Exists(originalPath))
    {
        Console.Error.WriteLine($"Original file not found: {originalPath}");
        return;
    }

    if (!File.Exists(processedPath))
    {
        Console.Error.WriteLine($"Processed file not found: {processedPath}");
        return;
    }

    using var originalReader = new AudioFileReader(originalPath);
    using var processedReader = new AudioFileReader(processedPath);

    if (!originalReader.WaveFormat.Equals(processedReader.WaveFormat))
    {
        Console.Error.WriteLine("WAV formats do not match. Skipping residual comparison.");
        return;
    }

    var originalSamples = ReadAllSamples(originalReader);
    var processedSamples = ReadAllSamples(processedReader);

    if (originalSamples.Length != processedSamples.Length)
    {
        Console.Error.WriteLine("Input lengths differ. Skipping residual comparison.");
        return;
    }

    var residual = new float[originalSamples.Length];
    double sumSqOriginal = 0;
    double sumSqProcessed = 0;
    double sumSqResidual = 0;
    double peakResidual = 0;

    for (int i = 0; i < originalSamples.Length; i++)
    {
        float originalSample = originalSamples[i];
        float processedSample = processedSamples[i];
        float difference = originalSample - processedSample;
        residual[i] = difference;

        sumSqOriginal += originalSample * originalSample;
        sumSqProcessed += processedSample * processedSample;
        sumSqResidual += difference * difference;
        peakResidual = Math.Max(peakResidual, Math.Abs(difference));
    }

    double rmsOriginal = Math.Sqrt(sumSqOriginal / originalSamples.Length);
    double rmsProcessed = Math.Sqrt(sumSqProcessed / processedSamples.Length);
    double rmsResidual = Math.Sqrt(sumSqResidual / residual.Length);
    double snr = rmsResidual > 0 ? 20.0 * Math.Log10(rmsOriginal / rmsResidual) : double.PositiveInfinity;

    string outputDirectory = Path.GetDirectoryName(processedPath) ?? Directory.GetCurrentDirectory();
    string baseName = Path.GetFileNameWithoutExtension(processedPath);
    string residualPath = Path.Combine(outputDirectory, $"{baseName}-residual.wav");
    string summaryPath = Path.Combine(outputDirectory, $"{baseName}-residual-summary.txt");

    using (var writer = new WaveFileWriter(residualPath, originalReader.WaveFormat))
    {
        writer.WriteSamples(residual, 0, residual.Length);
    }

    using (var summary = new StreamWriter(summaryPath, false))
    {
        summary.WriteLine("Residual comparison: original vs. processed output");
        summary.WriteLine($"Original: {Path.GetFileName(originalPath)}");
        summary.WriteLine($"Processed: {Path.GetFileName(processedPath)}");
        summary.WriteLine($"Residual: {Path.GetFileName(residualPath)}");
        summary.WriteLine($"SampleRate: {originalReader.WaveFormat.SampleRate}");
        summary.WriteLine($"Channels: {originalReader.WaveFormat.Channels}");
        summary.WriteLine($"TotalSamples: {originalSamples.Length}");
        summary.WriteLine($"RMS Original: {FormatDouble(rmsOriginal)}");
        summary.WriteLine($"RMS Processed: {FormatDouble(rmsProcessed)}");
        summary.WriteLine($"RMS Residual: {FormatDouble(rmsResidual)}");
        summary.WriteLine($"Peak Residual: {FormatDouble(peakResidual)}");
        summary.WriteLine($"SNR (dB): {FormatDouble(snr)}");
    }

    Console.WriteLine($"Residual comparison written: {residualPath}");
    Console.WriteLine($"Residual summary written: {summaryPath}");
}

static void CompareWavs(string originalPath, string processedPath)
{
    if (!File.Exists(originalPath))
    {
        Console.Error.WriteLine($"Original file not found: {originalPath}");
        Environment.Exit(1);
    }

    if (!File.Exists(processedPath))
    {
        Console.Error.WriteLine($"Processed file not found: {processedPath}");
        Environment.Exit(1);
    }

    using var originalReader = new AudioFileReader(originalPath);
    using var processedReader = new AudioFileReader(processedPath);

    if (!originalReader.WaveFormat.Equals(processedReader.WaveFormat))
    {
        Console.Error.WriteLine("WAV formats do not match. Comparison requires identical sample rate, channels and bit depth.");
        Environment.Exit(1);
    }

    var originalSamples = ReadAllSamples(originalReader);
    var processedSamples = ReadAllSamples(processedReader);

    if (originalSamples.Length != processedSamples.Length)
    {
        Console.Error.WriteLine("Input lengths differ. Comparison requires WAV files of equal length.");
        Environment.Exit(1);
    }

    var residual = new float[originalSamples.Length];
    double sumSqOriginal = 0;
    double sumSqProcessed = 0;
    double sumSqResidual = 0;
    double peakResidual = 0;

    for (int i = 0; i < originalSamples.Length; i++)
    {
        float originalSample = originalSamples[i];
        float processedSample = processedSamples[i];
        float invertedProcessed = -processedSample;
        float difference = originalSample + invertedProcessed;
        residual[i] = difference;

        sumSqOriginal += originalSample * originalSample;
        sumSqProcessed += processedSample * processedSample;
        sumSqResidual += difference * difference;
        peakResidual = Math.Max(peakResidual, Math.Abs(difference));
    }

    double rmsOriginal = Math.Sqrt(sumSqOriginal / originalSamples.Length);
    double rmsProcessed = Math.Sqrt(sumSqProcessed / processedSamples.Length);
    double rmsResidual = Math.Sqrt(sumSqResidual / residual.Length);
    double snr = rmsResidual > 0 ? 20.0 * Math.Log10(rmsOriginal / rmsResidual) : double.PositiveInfinity;

    string outputDirectory = Path.GetDirectoryName(processedPath) ?? Directory.GetCurrentDirectory();
    string baseName = Path.GetFileNameWithoutExtension(processedPath);
    string residualPath = Path.Combine(outputDirectory, $"{baseName}-residual.wav");
    string summaryPath = Path.Combine(outputDirectory, $"{baseName}-comparison.txt");

    using (var writer = new WaveFileWriter(residualPath, originalReader.WaveFormat))
    {
        writer.WriteSamples(residual, 0, residual.Length);
    }

    using (var summary = new StreamWriter(summaryPath, false))
    {
        summary.WriteLine("WAV comparison: original vs. phase-inverted processed output");
        summary.WriteLine($"Original: {Path.GetFileName(originalPath)}");
        summary.WriteLine($"Processed: {Path.GetFileName(processedPath)}");
        summary.WriteLine($"Residual: {Path.GetFileName(residualPath)}");
        summary.WriteLine($"SampleRate: {originalReader.WaveFormat.SampleRate}");
        summary.WriteLine($"Channels: {originalReader.WaveFormat.Channels}");
        summary.WriteLine($"TotalSamples: {originalSamples.Length}");
        summary.WriteLine($"RMS Original: {FormatDouble(rmsOriginal)}");
        summary.WriteLine($"RMS Processed: {FormatDouble(rmsProcessed)}");
        summary.WriteLine($"RMS Residual: {FormatDouble(rmsResidual)}");
        summary.WriteLine($"Peak Residual: {FormatDouble(peakResidual)}");
        summary.WriteLine($"SNR (dB): {FormatDouble(snr)}");
    }

    Console.WriteLine("Comparison complete.");
    Console.WriteLine($"Residual WAV: {residualPath}");
    Console.WriteLine($"Comparison summary: {summaryPath}");
}

static string FormatDouble(double value)
{
    return value.ToString("0.0000", CultureInfo.InvariantCulture);
}

static string FindWorkspaceRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);

    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "TestWAVs")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}
