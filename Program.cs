using NAudio.Wave;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Pulsar.Psycho;

const int PulsarWorkingSampleRate = 44100;
const int PulsarJointStereoThresholdKbps = 160;

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

var cliArgs = new List<string>(args);
int? vbrQuality = null;
for (int i = 0; i < cliArgs.Count; i++)
{
    if (cliArgs[i] != "-V" && cliArgs[i] != "--quality")
    {
        continue;
    }

    if (i + 1 >= cliArgs.Count || !int.TryParse(cliArgs[i + 1], out int parsedQuality) || parsedQuality < 0 || parsedQuality > 9)
    {
        Console.Error.WriteLine("Invalid VBR quality. Use -V <0-9> with 0 = best quality and 9 = strongest compression.");
        Environment.Exit(1);
        return;
    }

    vbrQuality = parsedQuality;
    cliArgs.RemoveAt(i + 1);
    cliArgs.RemoveAt(i);
    break;
}

args = cliArgs.ToArray();

bool useVbrPlsrMode = args.Length >= 4 && args[0] == "--vbrplsr";
bool useVbrPlsrPcmMode = args.Length >= 3 && args[0] == "--vbrplsrpcm";
bool useDecodePlsrMode = args.Length >= 3 && args[0] == "--decodeplsr";
bool useVbrMode = args.Length >= 3 && args[0] == "--vbr";
bool useLegacyMode = args.Length >= 3 && args[0] == "--legacy";
bool useLegacyPlannerMode = args.Length >= 3 && args[0] == "--legacyP";
bool useLegacyPlannerFastMode = args.Length >= 3 && args[0] == "--legacyP-fast";
int legacyBlockSize = PulsarBlockLadder.DefaultBlockSize;
int effectiveVbrQuality = vbrQuality ?? 4;
int targetKbps = QualityToNominalKbps(effectiveVbrQuality);
string inputPath;
string outputPath;
string? decodedOutputPath = null;

if (useVbrPlsrMode)
{
	inputPath = Path.GetFullPath(args[1]);
	outputPath = Path.GetFullPath(args[2]);
	decodedOutputPath = Path.GetFullPath(args[3]);
}
else if (useVbrPlsrPcmMode)
{
	inputPath = Path.GetFullPath(args[1]);
	outputPath = Path.GetFullPath(args[2]);
}
else if (useDecodePlsrMode)
{
	inputPath = Path.GetFullPath(args[1]);
	outputPath = Path.GetFullPath(args[2]);
}
else if (useVbrMode)
{
	inputPath = Path.GetFullPath(args[1]);
	outputPath = Path.GetFullPath(args[2]);
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
if (decodedOutputPath is not null)
{
	PrepareOutputFolder(decodedOutputPath);
	decodedOutputPath = ResolveWritableOutputPath(decodedOutputPath);
}

if (useDecodePlsrMode)
{
	var archive = File.ReadAllBytes(inputPath);
	var decoded = PulsarSuperframeArchiveCodec.DecodeArchive(archive);
	var decodedArchiveFormat = WaveFormat.CreateIeeeFloatWaveFormat(decoded.SampleRate, decoded.Channels);
	using (var writer = new WaveFileWriter(outputPath, decodedArchiveFormat))
	{
		writer.WriteSamples(decoded.Samples, 0, decoded.Samples.Length);
	}

	Console.WriteLine($"PULSAR archive decoded: {outputPath}");
	return;
}

var inputAudio = ReadAudioFile(inputPath);
int channels = inputAudio.WaveFormat.Channels;
int inputSampleRate = inputAudio.WaveFormat.SampleRate;
int sampleRate = PulsarWorkingSampleRate;

var samples = ResampleInterleaved(inputAudio.Samples, channels, inputSampleRate, sampleRate);
object ProgressLock = new();
float[] processed;
PulsarPlanner[]? planners = null;
List<PulsarFrameAllocation>[]? allocationsByChannel = null;
byte[]? pulsrArchive = null;

if (useVbrPlsrMode)
{
	processed = Array.Empty<float>();
}
else if (useVbrPlsrPcmMode)
{
	pulsrArchive = PulsarSuperframeArchiveCodec.EncodeSpectralArchive(samples, sampleRate, channels, targetKbps, effectiveVbrQuality);
	processed = Array.Empty<float>();
}
else if (useLegacyMode)
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
	(processed, planners, allocationsByChannel) = ProcessWithPulsar(samples, channels, sampleRate, targetKbps, effectiveVbrQuality);
}

if (useVbrPlsrMode)
{
	byte[] archive = PulsarSuperframeArchiveCodec.EncodeSpectralArchive(samples, sampleRate, channels, targetKbps, effectiveVbrQuality);
	File.WriteAllBytes(outputPath, archive);

	var decoded = PulsarSuperframeArchiveCodec.DecodeArchive(archive);
	var decodedFormat = WaveFormat.CreateIeeeFloatWaveFormat(decoded.SampleRate, decoded.Channels);
	using (var writer = new WaveFileWriter(decodedOutputPath!, decodedFormat))
	{
		writer.WriteSamples(decoded.Samples, 0, decoded.Samples.Length);
	}

	WriteResidualComparison(inputPath, decodedOutputPath!);

	double seconds = decoded.Samples.Length / (double)(decoded.SampleRate * decoded.Channels);
	double avgKbps = archive.Length * 8.0 / Math.Max(seconds, 1e-9) / 1000.0;
	Console.WriteLine($"PULSAR archive encoded: {outputPath} ({avgKbps:0.00} kbps)");
	Console.WriteLine($"PULSAR archive decoded WAV: {decodedOutputPath}");
	return;
}

if (useVbrPlsrPcmMode)
{
	File.WriteAllBytes(outputPath, pulsrArchive!);
	Console.WriteLine($"PULSAR PCM archive written: {outputPath}");
	return;
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
    : useVbrPlsrPcmMode
        ? $"PULSAR spectral PCM render complete: {outputPath}"
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

static (float[] Samples, WaveFormat WaveFormat) ReadAudioFile(string path)
{
    string extension = Path.GetExtension(path);
    if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
    {
        using var waveReader = new WaveFileReader(path);
        return (ReadAllWaveSamples(waveReader), waveReader.WaveFormat);
    }

    if (string.Equals(extension, ".pulsr", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".plsr", StringComparison.OrdinalIgnoreCase))
    {
        byte[] archive = File.ReadAllBytes(path);
        var decoded = PulsarSuperframeArchiveCodec.DecodeArchive(archive);
        return (decoded.Samples, WaveFormat.CreateIeeeFloatWaveFormat(decoded.SampleRate, decoded.Channels));
    }

    using var audioReader = new AudioFileReader(path);
    return (ReadAllSamples(audioReader, audioReader.WaveFormat), audioReader.WaveFormat);
}

static float[] ReadAllSamples(ISampleProvider reader, WaveFormat waveFormat)
{
    var allSamples = new List<float>();
    var buffer = new float[waveFormat.SampleRate * waveFormat.Channels];

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

static float[] ReadAllWaveSamples(WaveFileReader reader)
{
    var format = reader.WaveFormat;
    int bytesPerSample = format.BitsPerSample / 8;
    int frameSize = format.BlockAlign;
    if (bytesPerSample <= 0 || frameSize <= 0)
    {
        throw new InvalidOperationException($"Unsupported WAV format: {format.Encoding}, {format.BitsPerSample}-bit.");
    }

    var allSamples = new List<float>((int)(reader.Length / Math.Max(1, bytesPerSample)));
    byte[] buffer = new byte[Math.Max(frameSize * 4096, frameSize)];

    int read;
    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        int framesRead = read / frameSize;
        int offset = 0;

        for (int frame = 0; frame < framesRead; frame++)
        {
            for (int channel = 0; channel < format.Channels; channel++)
            {
                allSamples.Add(ReadSample(buffer, offset, format.Encoding, format.BitsPerSample));
                offset += bytesPerSample;
            }
        }
    }

    return allSamples.ToArray();
}

static float[] ResampleInterleaved(float[] samples, int channels, int sourceSampleRate, int targetSampleRate)
{
    if (channels <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(channels));
    }

    if (sourceSampleRate <= 0 || targetSampleRate <= 0)
    {
        throw new ArgumentOutOfRangeException(sourceSampleRate <= 0 ? nameof(sourceSampleRate) : nameof(targetSampleRate));
    }

    if (sourceSampleRate == targetSampleRate || samples.Length == 0)
    {
        return samples;
    }

    int sourceFrames = samples.Length / channels;
    int targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (targetSampleRate / (double)sourceSampleRate)));
    float[] resampled = new float[targetFrames * channels];
    double frameScale = sourceSampleRate / (double)targetSampleRate;

    for (int targetFrame = 0; targetFrame < targetFrames; targetFrame++)
    {
        double sourcePosition = targetFrame * frameScale;
        int leftFrame = Math.Clamp((int)Math.Floor(sourcePosition), 0, sourceFrames - 1);
        int rightFrame = Math.Clamp(leftFrame + 1, 0, sourceFrames - 1);
        float t = (float)(sourcePosition - leftFrame);

        for (int channel = 0; channel < channels; channel++)
        {
            float left = samples[(leftFrame * channels) + channel];
            float right = samples[(rightFrame * channels) + channel];
            resampled[(targetFrame * channels) + channel] = left + ((right - left) * t);
        }
    }

    return resampled;
}

static float ReadSample(byte[] buffer, int offset, WaveFormatEncoding encoding, int bitsPerSample)
{
    return encoding switch
    {
        WaveFormatEncoding.Pcm when bitsPerSample == 16 => BitConverter.ToInt16(buffer, offset) / 32768f,
        WaveFormatEncoding.Pcm when bitsPerSample == 24 => ReadPcm24(buffer, offset) / 8388608f,
        WaveFormatEncoding.Pcm when bitsPerSample == 32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
        WaveFormatEncoding.Extensible when bitsPerSample == 16 => BitConverter.ToInt16(buffer, offset) / 32768f,
        WaveFormatEncoding.Extensible when bitsPerSample == 24 => ReadPcm24(buffer, offset) / 8388608f,
        WaveFormatEncoding.Extensible when bitsPerSample == 32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
        WaveFormatEncoding.IeeeFloat when bitsPerSample == 32 => BitConverter.ToSingle(buffer, offset),
        WaveFormatEncoding.IeeeFloat when bitsPerSample == 64 => (float)BitConverter.ToDouble(buffer, offset),
        _ => throw new InvalidOperationException($"Unsupported WAV encoding: {encoding}, {bitsPerSample}-bit.")
    };
}

static int ReadPcm24(byte[] buffer, int offset)
{
    int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
    if ((value & 0x800000) != 0)
    {
        value |= unchecked((int)0xFF000000);
    }

    return value;
}

static (float[] Processed, PulsarPlanner[] Planners, List<PulsarFrameAllocation>[] Allocations) ProcessWithPulsar(float[] interleavedSamples, int channels, int sampleRate, int targetKbps, int vbrQuality)
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

	bool useMidSideStereo = channels == 2 && targetKbps <= PulsarJointStereoThresholdKbps;
	if (useMidSideStereo)
	{
		ApplyMidSideStereo(channelBuffers[0], channelBuffers[1]);
	}

	var processedChannels = new float[channels][];
	var allocations = new List<PulsarFrameAllocation>[channels];
	var allocator = new PulsarAllocator(new PulsarAllocationConfig
	{
		Quality = vbrQuality,
		SampleRate = sampleRate,
		HopSize = PulsarBlockLadder.ControlHopSize,
	});

	Parallel.For(0, channels, channel =>
	{
		var result = PulsarTransformEngine.ProcessWithPlans(channelBuffers[channel], planners[channel]);
		var psycho = new PulsarPsycho(new PulsarPsychoSettings
		{
			SampleRate = sampleRate,
			FftSize = 2048,
			HopSize = PulsarBlockLadder.ControlHopSize,
		});
		var psychoFrames = psycho.AnalyzeSong(channelBuffers[channel]);
		allocations[channel] = allocator.AllocateSong(result.Plans, psychoFrames);
		processedChannels[channel] = PulsarTransformEngine.ProcessWithBitAllocation(
			channelBuffers[channel],
			result.Plans,
			allocations[channel],
			psychoFrames);
	});

	var output = new float[interleavedSamples.Length];
	if (useMidSideStereo && channels == 2)
	{
		for (int frame = 0; frame < frames; frame++)
		{
			float mid = processedChannels[0][frame];
			float side = processedChannels[1][frame];
			output[frame * channels] = mid + side;
			output[(frame * channels) + 1] = mid - side;
		}

		return (output, planners, allocations);
	}

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

static void ApplyMidSideStereo(float[] left, float[] right)
{
	if (left.Length != right.Length)
	{
		throw new InvalidOperationException("Mid/Side stereo requires matching channel lengths.");
	}

	for (int i = 0; i < left.Length; i++)
	{
		float l = left[i];
		float r = right[i];
		left[i] = 0.5f * (l + r);
		right[i] = 0.5f * (l - r);
	}
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
    long totalAllocatedBits = 0;
    int maxSegmentCount = 0;

    foreach (var allocations in allocationsByChannel)
    {
        maxSegmentCount = Math.Max(maxSegmentCount, allocations.Count);
        foreach (PulsarFrameAllocation allocation in allocations)
        {
            totalAllocatedBits += allocation.TargetBits;
        }
    }

    double totalDurationSeconds = maxSegmentCount * (double)PulsarBlockLadder.ControlHopSize / sampleRate;
    double actualAverageKbps = totalDurationSeconds > 0.0
        ? totalAllocatedBits / totalDurationSeconds / 1000.0
        : 0.0;

    string logPath = Path.Combine(
        Path.GetDirectoryName(outputPath)!,
        $"{Path.GetFileNameWithoutExtension(outputPath)}.allocation.log.txt");

    using var writer = new StreamWriter(logPath, false);
    writer.WriteLine($"Pulsar allocation log for {Path.GetFileName(outputPath)}");
    writer.WriteLine($"SampleRate={sampleRate}");
    writer.WriteLine($"TargetKbps={targetKbps}");
    writer.WriteLine($"AllocatedBits={totalAllocatedBits}");
    writer.WriteLine($"ActualAverageKbps={FormatDouble(actualAverageKbps)}");
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
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- -V <0-9> --vbr <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- -V <0-9> --vbrplsr <input.wav> <output.pulsr> <decoded.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- -V <0-9> --vbrplsrpcm <input.wav> <output.pulsr>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --decodeplsr <input.pulsr> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacy <input.wav> <output.wav> [blockSize]");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacyP <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacyP-fast <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --compare <original.wav> <processed.wav>");
    Console.WriteLine();
    Console.WriteLine("Legacy mode renders a fixed stationary path without using the planner.");
    Console.WriteLine("LegacyP mode renders using the planner decisions, but without planner switching blending.");
    Console.WriteLine("-V 0 = beste Qualitaet, -V 9 = staerkste Kompression.");
    Console.WriteLine("Valid block sizes: " + string.Join(", ", PulsarBlockLadder.Steps));
}

static int QualityToNominalKbps(int quality)
{
    int[] nominalKbps = [320, 288, 256, 224, 192, 160, 128, 112, 96, 80];
    return nominalKbps[Math.Clamp(quality, 0, nominalKbps.Length - 1)];
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

    var originalReader = ReadAudioFile(originalPath);
    var processedReader = ReadAudioFile(processedPath);

    if (originalReader.WaveFormat.Channels != processedReader.WaveFormat.Channels)
    {
        Console.Error.WriteLine("Channel count does not match. Skipping residual comparison.");
        return;
    }

    var originalSamples = ResampleInterleaved(
        originalReader.Samples,
        originalReader.WaveFormat.Channels,
        originalReader.WaveFormat.SampleRate,
        processedReader.WaveFormat.SampleRate);
    var processedSamples = processedReader.Samples;

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
        summary.WriteLine($"SampleRate: {processedReader.WaveFormat.SampleRate}");
        summary.WriteLine($"Channels: {processedReader.WaveFormat.Channels}");
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

    var originalReader = ReadAudioFile(originalPath);
    var processedReader = ReadAudioFile(processedPath);

    if (originalReader.WaveFormat.Channels != processedReader.WaveFormat.Channels)
    {
        Console.Error.WriteLine("WAV channel count does not match. Comparison requires identical channel count.");
        Environment.Exit(1);
    }

    var originalSamples = ResampleInterleaved(
        originalReader.Samples,
        originalReader.WaveFormat.Channels,
        originalReader.WaveFormat.SampleRate,
        processedReader.WaveFormat.SampleRate);
    var processedSamples = processedReader.Samples;

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
        summary.WriteLine($"SampleRate: {processedReader.WaveFormat.SampleRate}");
        summary.WriteLine($"Channels: {processedReader.WaveFormat.Channels}");
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
