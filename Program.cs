using NAudio.Wave;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System;
using Pulsar.Psycho;

const int PulsarWorkingSampleRate = 44100;
const int PulsarJointStereoThresholdKbps = 160;

var workspaceRoot = FindWorkspaceRoot();
var artifactsRoot = Path.Combine(workspaceRoot, "Artifacts");
var artifactsOutputRoot = Path.Combine(artifactsRoot, "Output");
var artifactsBypassRoot = Path.Combine(artifactsOutputRoot, "Packer-Bypass");
var artifactsLegacyRoot = Path.Combine(artifactsOutputRoot, "Legacy");
var artifactsPackerRoot = Path.Combine(artifactsOutputRoot, "Packer");
Directory.CreateDirectory(artifactsOutputRoot);
Directory.CreateDirectory(artifactsBypassRoot);
Directory.CreateDirectory(artifactsLegacyRoot);
Directory.CreateDirectory(artifactsPackerRoot);

var defaultInputPath = Path.Combine(workspaceRoot, "TestWAVs", "Strike A Pose! 30s.wav");
var defaultOutputPath = Path.Combine(artifactsOutputRoot, "Strike A Pose! 30s pulsar-process.wav");

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
    if (cliArgs[i] != "-V" && cliArgs[i] != "--quality") continue;

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
bool useDecodePlsrMode = args.Length >= 3 && args[0] == "--decodeplsr";
bool useVbrMode = args.Length >= 3 && args[0] == "--vbr";
bool useLegacyMode = args.Length >= 3 && args[0] == "--legacy";
bool useLegacyPlanMode = args.Length >= 3 && args[0] == "--legacyplan";

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
else if (useDecodePlsrMode || useVbrMode || useLegacyMode || useLegacyPlanMode)
{
    inputPath = Path.GetFullPath(args[1]);
    outputPath = Path.GetFullPath(args[2]);

    if ((useLegacyMode || useLegacyPlanMode) && args.Length > 3)
    {
        if (!int.TryParse(args[3], out legacyBlockSize) || !PulsarBlockLadder.IsValidBlockSize(legacyBlockSize))
        {
            Console.Error.WriteLine("Invalid legacy block size. Use one of: " + string.Join(", ", PulsarBlockLadder.Steps));
            Environment.Exit(1);
            return;
        }
    }
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

if (useVbrPlsrMode)
{
    string runFolder = CreateModeRunFolder(artifactsPackerRoot, inputPath, $"v{effectiveVbrQuality}");
    string baseName = Path.GetFileNameWithoutExtension(inputPath);
    string outputFileName = string.IsNullOrEmpty(baseName) ? $"output-v{effectiveVbrQuality}.pulsr" : $"{baseName}-v{effectiveVbrQuality}.pulsr";
    outputPath = Path.Combine(runFolder, outputFileName);

    if (decodedOutputPath is not null)
    {
        string decodedFileName = string.IsNullOrEmpty(baseName) ? $"decoded-v{effectiveVbrQuality}.wav" : $"{baseName}-v{effectiveVbrQuality}-decoded.wav";
        decodedOutputPath = Path.Combine(runFolder, decodedFileName);
    }
}
else if (useVbrMode || useLegacyMode || useLegacyPlanMode)
{
    string modeRoot = useLegacyMode || useLegacyPlanMode ? artifactsLegacyRoot : artifactsBypassRoot;
    string runLabel = useLegacyPlanMode ? "legacy-plan" : useLegacyMode ? "legacy" : $"v{effectiveVbrQuality}";
    string runFolder = CreateModeRunFolder(modeRoot, inputPath, runLabel);
    string baseName = Path.GetFileNameWithoutExtension(inputPath);
    string outputFileName = string.IsNullOrEmpty(baseName)
        ? useLegacyPlanMode ? "legacy-plan-output.wav" : useLegacyMode ? "legacy-output.wav" : $"output-v{effectiveVbrQuality}.wav"
        : useLegacyPlanMode ? $"{baseName}-legacy-plan.wav" : useLegacyMode ? $"{baseName}-legacy.wav" : $"{baseName}-v{effectiveVbrQuality}.wav";
    outputPath = Path.Combine(runFolder, outputFileName);
}
else
{
    outputPath = EnsureArtifactOutputPath(outputPath, artifactsOutputRoot);
}

if (decodedOutputPath is not null && !useVbrPlsrMode)
{
    decodedOutputPath = EnsureArtifactOutputPath(decodedOutputPath, artifactsOutputRoot);
}

PrepareOutputFolder(outputPath);
outputPath = ResolveWritableOutputPath(outputPath);
if (decodedOutputPath is not null)
{
    PrepareOutputFolder(decodedOutputPath);
    decodedOutputPath = ResolveWritableOutputPath(decodedOutputPath);
}

// --- DECODE ONLY MODE ---
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
int sampleRate = useLegacyMode || useLegacyPlanMode ? inputSampleRate : PulsarWorkingSampleRate;

var samples = sampleRate == inputSampleRate
    ? inputAudio.Samples
    : ResampleInterleaved(inputAudio.Samples, channels, inputSampleRate, sampleRate);
float[] processed;
double? engineProcessingSeconds = null;
double? plannerProcessingSeconds = null;
double? totalProcessingSeconds = null;

// --- FULL ARCHIVE ENCODE/DECODE MODE ---
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

    WriteResidualComparison(inputPath, decodedOutputPath!, "packer");
    AppendArchiveSummary(decodedOutputPath!, outputPath, archive.Length);
    WritePlannerLogs(outputPath, sampleRate, BuildArchiveStylePlanners(samples, channels, targetKbps));
    double seconds = decoded.Samples.Length / (double)(decoded.SampleRate * decoded.Channels);
    double avgKbps = archive.Length * 8.0 / Math.Max(seconds, 1e-9) / 1000.0;
    Console.WriteLine($"PULSAR archive encoded: {outputPath} ({avgKbps:0.00} kbps)");
    Console.WriteLine($"PULSAR archive decoded WAV: {decodedOutputPath}");
    return;
}

// --- RAW PROCESSING MODES (Legacy or VBR) ---
PulsarPlanner[]? planners = null;
List<PulsarFrameAllocation>[]? allocationsByChannel = null;

if (useLegacyPlanMode)
{
    Console.WriteLine($"Running Pure MDCT Legacy Mode with frame planning...");
    planners = new PulsarPlanner[channels];
    for (int channel = 0; channel < channels; channel++) planners[channel] = new PulsarPlanner();
    var timedResult = ProcessLegacyPlanner(samples, channels, planners);
    processed = timedResult.Output;
    plannerProcessingSeconds = timedResult.PlannerSeconds;
    engineProcessingSeconds = timedResult.RenderSeconds;
    totalProcessingSeconds = timedResult.TotalSeconds;
}
else if (useLegacyMode)
{
    Console.WriteLine($"Running Pure MDCT Legacy Mode (BlockSize: {legacyBlockSize})...");
    var engineStopwatch = Stopwatch.StartNew();
    processed = ProcessLegacy(samples, channels, legacyBlockSize);
    engineStopwatch.Stop();
    engineProcessingSeconds = engineStopwatch.Elapsed.TotalSeconds;
    totalProcessingSeconds = engineProcessingSeconds;
}
else
{
    Console.WriteLine($"Running VBR Spectral Process (Quality: {effectiveVbrQuality}) using archive-style quantization...");
    var engineStopwatch = Stopwatch.StartNew();
    processed = PulsarSuperframeArchiveCodec.RenderSpectralPcm(samples, sampleRate, channels, targetKbps, effectiveVbrQuality).Samples;
    engineStopwatch.Stop();
    engineProcessingSeconds = engineStopwatch.Elapsed.TotalSeconds;
    totalProcessingSeconds = engineProcessingSeconds;
}

var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
using (var writer = new WaveFileWriter(outputPath, outputFormat))
{
    writer.WriteSamples(processed, 0, processed.Length);
}

WriteResidualComparison(
    inputPath,
    outputPath,
    useLegacyPlanMode ? "legacyplan" : useLegacyMode ? "legacy" : "bypass",
    engineProcessingSeconds,
    plannerProcessingSeconds,
    totalProcessingSeconds);

if (planners is not null)
{
    WritePlannerLogs(outputPath, sampleRate, planners);
    if (allocationsByChannel is not null) WriteAllocationLogs(outputPath, sampleRate, channels, allocationsByChannel, targetKbps);
}
else if (useVbrMode)
{
    WritePlannerLogs(outputPath, sampleRate, BuildArchiveStylePlanners(samples, channels, targetKbps));
}

Console.WriteLine(useLegacyMode 
    ? $"Legacy pure MDCT render complete: {outputPath} (blockSize={legacyBlockSize})" 
    : $"Pulsar process render complete: {outputPath}");


// ==============================================================================
// HELPERS
// ==============================================================================

static void PrepareOutputFolder(string outputPath)
{
    string outputDirectory = Path.GetDirectoryName(outputPath)!;
    Directory.CreateDirectory(outputDirectory);
}

static string ResolveWritableOutputPath(string outputPath)
{
    if (!File.Exists(outputPath)) return outputPath;
    try { using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return outputPath; }
    catch { return BuildTimestampedPath(outputPath); }
}

static string BuildTimestampedPath(string outputPath)
{
    string directory = Path.GetDirectoryName(outputPath)!;
    string baseName = Path.GetFileNameWithoutExtension(outputPath);
    string extension = Path.GetExtension(outputPath);
    string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    return Path.Combine(directory, $"{baseName}-{timestamp}{extension}");
}

static string EnsureArtifactOutputPath(string requestedOutputPath, string artifactsOutputRoot)
{
    string requestedDirectory = Path.GetDirectoryName(requestedOutputPath)!;
    string filename = Path.GetFileName(requestedOutputPath);

    if (requestedDirectory.StartsWith(artifactsOutputRoot, StringComparison.OrdinalIgnoreCase))
    {
        return requestedOutputPath;
    }

    return Path.Combine(artifactsOutputRoot, filename);
}

static string CreateModeRunFolder(string modeRoot, string inputPath, string? runLabel = null)
{
    string baseName = Path.GetFileNameWithoutExtension(inputPath);
    string safeName = string.IsNullOrWhiteSpace(baseName) ? "run" : baseName;
    string labelSuffix = string.IsNullOrWhiteSpace(runLabel) ? string.Empty : $"-{runLabel}";
    string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    string runFolder = Path.Combine(modeRoot, $"{safeName}{labelSuffix}-{timestamp}");
    Directory.CreateDirectory(runFolder);
    return runFolder;
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
        for (int i = 0; i < read; i++) allSamples.Add(buffer[i]);
    return allSamples.ToArray();
}

static float[] ReadAllWaveSamples(WaveFileReader reader)
{
    var format = reader.WaveFormat;
    int bytesPerSample = format.BitsPerSample / 8;
    int frameSize = format.BlockAlign;
    if (bytesPerSample <= 0 || frameSize <= 0) throw new InvalidOperationException($"Unsupported WAV format.");

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
    if (sourceSampleRate == targetSampleRate || samples.Length == 0) return samples;
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
    if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
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
        for (int channel = 0; channel < channels; channel++)
            channelBuffers[channel][frame] = interleavedSamples[frame * channels + channel];

    bool useMidSideStereo = channels == 2 && targetKbps <= PulsarJointStereoThresholdKbps;
    if (useMidSideStereo) ApplyMidSideStereo(channelBuffers[0], channelBuffers[1]);

    var processedChannels = new float[channels][];
    var allocations = new List<PulsarFrameAllocation>[channels];
    var allocator = new PulsarAllocator(new PulsarAllocationConfig { Quality = vbrQuality, SampleRate = sampleRate, HopSize = PulsarBlockLadder.ControlHopSize });

    Parallel.For(0, channels, channel =>
    {
        List<PulsarFramePlan> renderPlans = planners[channel].PlanLegacyRenderSong(channelBuffers[channel]);

        var psycho = new PulsarPsycho(new PulsarPsychoSettings { SampleRate = sampleRate, FftSize = 2048, HopSize = PulsarBlockLadder.ControlHopSize });
        var psychoFrames = psycho.AnalyzeSong(channelBuffers[channel]);
        allocations[channel] = allocator.AllocateSong(planners[channel].LastPlan, psychoFrames);
        
        processedChannels[channel] = PulsarTransformEngine.ProcessWithBitAllocation(channelBuffers[channel], renderPlans, allocations[channel], psychoFrames);
    });

    var output = new float[interleavedSamples.Length];
    if (useMidSideStereo && channels == 2)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            float mid = processedChannels[0][frame], side = processedChannels[1][frame];
            output[frame * channels] = mid + side;
            output[(frame * channels) + 1] = mid - side;
        }
        return (output, planners, allocations);
    }

    for (int channel = 0; channel < channels; channel++)
    {
        float[] processedChannel = processedChannels[channel];
        for (int frame = 0; frame < frames; frame++)
            output[frame * channels + channel] = processedChannel[frame];
    }
    return (output, planners, allocations);
}

static void ApplyMidSideStereo(float[] left, float[] right)
{
    for (int i = 0; i < left.Length; i++)
    {
        float l = left[i], r = right[i];
        left[i] = 0.5f * (l + r);
        right[i] = 0.5f * (l - r);
    }
}

static float[] ProcessLegacy(float[] interleavedSamples, int channels, int blockSize)
{
    var channelBuffers = new float[channels][];
    int frames = interleavedSamples.Length / channels;
    for (int channel = 0; channel < channels; channel++) channelBuffers[channel] = new float[frames];

    for (int frame = 0; frame < frames; frame++)
        for (int channel = 0; channel < channels; channel++)
            channelBuffers[channel][frame] = interleavedSamples[frame * channels + channel];

    var processedChannels = new float[channels][];
    for (int channel = 0; channel < channels; channel++)
    {
        processedChannels[channel] = PulsarTransformEngine.ProcessLegacy(channelBuffers[channel], blockSize);
    }

    var output = new float[interleavedSamples.Length];
    for (int channel = 0; channel < channels; channel++)
    {
        float[] processedChannel = processedChannels[channel];
        for (int frame = 0; frame < frames; frame++) output[frame * channels + channel] = processedChannel[frame];
    }
    return output;
}

static (float[] Output, double PlannerSeconds, double RenderSeconds, double TotalSeconds) ProcessLegacyPlanner(float[] interleavedSamples, int channels, PulsarPlanner[] planners)
{
    var channelBuffers = new float[channels][];
    int frames = interleavedSamples.Length / channels;
    for (int channel = 0; channel < channels; channel++) channelBuffers[channel] = new float[frames];

    for (int frame = 0; frame < frames; frame++)
        for (int channel = 0; channel < channels; channel++)
            channelBuffers[channel][frame] = interleavedSamples[frame * channels + channel];

    var processedChannels = new float[channels][];
    double plannerSeconds = 0.0;
    double renderSeconds = 0.0;
    for (int channel = 0; channel < channels; channel++)
    {
        var timedResult = PulsarTransformEngine.ProcessLegacyPlannerWithTimings(channelBuffers[channel], planners[channel]);
        processedChannels[channel] = timedResult.Output;
        plannerSeconds += timedResult.PlannerSeconds;
        renderSeconds += timedResult.RenderSeconds;
    }

    var output = new float[interleavedSamples.Length];
    for (int channel = 0; channel < channels; channel++)
    {
        float[] processedChannel = processedChannels[channel];
        for (int frame = 0; frame < frames; frame++) output[frame * channels + channel] = processedChannel[frame];
    }
    return (output, plannerSeconds, renderSeconds, plannerSeconds + renderSeconds);
}

static PulsarPlanner[] BuildArchiveStylePlanners(float[] interleavedSamples, int channels, int targetKbps)
{
    if (channels <= 0) return Array.Empty<PulsarPlanner>();

    var channelBuffers = new float[channels][];
    int frames = interleavedSamples.Length / channels;
    for (int channel = 0; channel < channels; channel++) channelBuffers[channel] = new float[frames];

    for (int frame = 0; frame < frames; frame++)
        for (int channel = 0; channel < channels; channel++)
            channelBuffers[channel][frame] = interleavedSamples[frame * channels + channel];

    bool useMidSideStereo = channels == 2 && targetKbps <= PulsarJointStereoThresholdKbps;
    if (useMidSideStereo) ApplyMidSideStereo(channelBuffers[0], channelBuffers[1]);

    var sharedPlanner = new PulsarPlanner();
    sharedPlanner.PlanLegacyRenderSong(channelBuffers[0]);

    var planners = new PulsarPlanner[channels];
    for (int channel = 0; channel < channels; channel++)
    {
        planners[channel] = sharedPlanner;
    }

    return planners;
}

static void WritePlannerLogs(string outputPath, int sampleRate, IReadOnlyList<PulsarPlanner> planners)
{
    if (planners.Count == 0) return;

    string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();

    for (int channel = 0; channel < planners.Count; channel++)
    {
        PulsarPlanner planner = planners[channel];

        if (planner.LastAnalyses.Count > 0)
        {
            string analysisPath = Path.Combine(outputDirectory, $"planner-channel-{channel}-analysis.csv");
            using var writer = new StreamWriter(analysisPath, false);
            writer.WriteLine("segment_index,time_s,transient_level,attack_ratio,peak_delta_db,attack_index,energy_modulation,crest_factor,low_band_ratio,high_band_ratio,sustained_high_band_ratio,desired_ladder_position,clue_strength,pre_echo_risk,spectral_flux,sub_bass,bass,low_mid,mid,high_mid,presence,brilliance,centroid,flatness");
            foreach (PulsarTransientAnalysis analysis in planner.LastAnalyses)
            {
                double timeSeconds = analysis.SegmentIndex * (PulsarBlockLadder.ControlHopSize / (double)sampleRate);
                writer.WriteLine(string.Join(',',
                    analysis.SegmentIndex,
                    FormatDouble(timeSeconds),
                    analysis.Level,
                    FormatDouble(analysis.AttackRatio),
                    FormatDouble(analysis.PeakDeltaDb),
                    analysis.AttackIndex,
                    FormatDouble(analysis.EnergyModulation),
                    FormatDouble(analysis.CrestFactor),
                    FormatDouble(analysis.LowBandRatio),
                    FormatDouble(analysis.HighBandRatio),
                    FormatDouble(analysis.SustainedHighBandRatio),
                    FormatDouble(analysis.DesiredLadderPosition),
                    FormatDouble(analysis.ClueStrength),
                    FormatDouble(analysis.PreEchoRisk),
                    FormatDouble(analysis.SpectralFlux),
                    FormatDouble(analysis.Spectral.SubBass),
                    FormatDouble(analysis.Spectral.Bass),
                    FormatDouble(analysis.Spectral.LowMid),
                    FormatDouble(analysis.Spectral.Mid),
                    FormatDouble(analysis.Spectral.HighMid),
                    FormatDouble(analysis.Spectral.Presence),
                    FormatDouble(analysis.Spectral.Brilliance),
                    FormatDouble(analysis.Spectral.Centroid),
                    FormatDouble(analysis.Spectral.Flatness)));
            }
        }

        if (planner.LastPlan.Count > 0)
        {
            string planPath = Path.Combine(outputDirectory, $"planner-channel-{channel}-segments.csv");
            using var writer = new StreamWriter(planPath, false);
            writer.WriteLine("segment_index,time_s,previous_block_size,block_size,next_block_size,target_block_size,direction,transient_level,pre_echo_risk,spectral_flux,clue_strength,desired_ladder_position");
            foreach (PulsarFramePlan plan in planner.LastPlan)
            {
                double timeSeconds = plan.SegmentIndex * (PulsarBlockLadder.ControlHopSize / (double)sampleRate);
                writer.WriteLine(string.Join(',',
                    plan.SegmentIndex,
                    FormatDouble(timeSeconds),
                    plan.PreviousBlockSize,
                    plan.BlockSize,
                    plan.NextBlockSize,
                    plan.TargetBlockSize,
                    plan.Direction,
                    plan.TransientLevel,
                    FormatDouble(plan.PreEchoRisk),
                    FormatDouble(plan.SpectralFlux),
                    FormatDouble(plan.ClueStrength),
                    FormatDouble(plan.DesiredLadderPosition)));
            }
        }

        if (planner.LastLegacyRenderPlan.Count > 0)
        {
            string renderPath = Path.Combine(outputDirectory, $"planner-channel-{channel}-render.csv");
            using var writer = new StreamWriter(renderPath, false);
            writer.WriteLine("frame_index,start_time_s,segment_index,previous_block_size,block_size,next_block_size,target_block_size,direction,transient_level,pre_echo_risk,spectral_flux,clue_strength");

            int frameStartSamples = 0;
            for (int frameIndex = 0; frameIndex < planner.LastLegacyRenderPlan.Count; frameIndex++)
            {
                PulsarFramePlan plan = planner.LastLegacyRenderPlan[frameIndex];
                double timeSeconds = frameStartSamples / (double)sampleRate;
                writer.WriteLine(string.Join(',',
                    frameIndex,
                    FormatDouble(timeSeconds),
                    plan.SegmentIndex,
                    plan.PreviousBlockSize,
                    plan.BlockSize,
                    plan.NextBlockSize,
                    plan.TargetBlockSize,
                    plan.Direction,
                    plan.TransientLevel,
                    FormatDouble(plan.PreEchoRisk),
                    FormatDouble(plan.SpectralFlux),
                    FormatDouble(plan.ClueStrength)));

                int rightOverlap = Math.Min(plan.BlockSize / 2, plan.NextBlockSize / 2);
                frameStartSamples += plan.BlockSize - rightOverlap;
            }
        }
    }
}

static void WriteAllocationLogs(string outputPath, int sampleRate, int channels, IReadOnlyList<List<PulsarFrameAllocation>> allocationsByChannel, int targetKbps)
{
    if (allocationsByChannel.Count == 0) return;

    string summaryPath = GetSummaryPath(outputPath);

    long totalBits = 0;
    int totalFrames = 0;
    for (int channel = 0; channel < allocationsByChannel.Count; channel++)
    {
        var channelAllocations = allocationsByChannel[channel];
        totalFrames = Math.Max(totalFrames, channelAllocations.Count);
        foreach (var allocation in channelAllocations)
        {
            totalBits += allocation.BandBits.Sum();
        }
    }

    double seconds = totalFrames * (PulsarBlockLadder.ControlHopSize / (double)sampleRate);
    double totalKbits = totalBits / 1000.0;
    double avgKbps = seconds > 0 ? totalKbits / seconds : 0.0;

    using (var writer = new StreamWriter(summaryPath, true))
    {
        writer.WriteLine();
        writer.WriteLine("--- Allocation Summary ---");
        writer.WriteLine($"Target nominal kbps: {targetKbps}");
        writer.WriteLine($"Channels: {allocationsByChannel.Count}");
        writer.WriteLine($"Frames (control hops): {totalFrames}");
        writer.WriteLine($"Total allocated audio bits: {totalBits}");
        writer.WriteLine($"Total allocated audio kbits: {FormatDouble(totalKbits)}");
        writer.WriteLine($"Estimated pure audio bitrate kbps: {FormatDouble(avgKbps)}");
        writer.WriteLine();
        writer.WriteLine("Per-channel allocation summary:");

        for (int channel = 0; channel < allocationsByChannel.Count; channel++)
        {
            var channelAllocations = allocationsByChannel[channel];
            long channelBits = 0;
            foreach (var allocation in channelAllocations)
            {
                channelBits += allocation.BandBits.Sum();
            }
            writer.WriteLine($"  Channel {channel}: {channelAllocations.Count} frames, {channelBits} bits");
        }
    }

    Console.WriteLine($"Summary appended: {summaryPath}");
    Console.WriteLine($"Estimated bypass-packer bitrate: {avgKbps:0.00} kbps");
}
static string FormatDouble(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacy <input.wav> <output.wav> [blockSize]");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --legacyplan <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- -V <0-9> --vbr <input.wav> <output.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- -V <0-9> --vbrplsr <input.wav> <output.pulsr> <decoded.wav>");
    Console.WriteLine("  dotnet run --project .\\PulsarCodec.csproj -- --decodeplsr <input.pulsr> <output.wav>");
}

static int QualityToNominalKbps(int quality) => new int[] { 320, 288, 256, 224, 192, 160, 128, 112, 96, 80 }[Math.Clamp(quality, 0, 9)];

static void WriteResidualComparison(
    string originalPath,
    string processedPath,
    string mode,
    double? engineProcessingSeconds = null,
    double? plannerProcessingSeconds = null,
    double? totalProcessingSeconds = null)
{
    if (!File.Exists(originalPath) || !File.Exists(processedPath)) return;
    var originalReader = ReadAudioFile(originalPath);
    var processedReader = ReadAudioFile(processedPath);
    if (originalReader.WaveFormat.Channels != processedReader.WaveFormat.Channels) return;

    var originalSamples = ResampleInterleaved(originalReader.Samples, originalReader.WaveFormat.Channels, originalReader.WaveFormat.SampleRate, processedReader.WaveFormat.SampleRate);
    var processedSamples = processedReader.Samples;
    if (originalSamples.Length != processedSamples.Length) return;

    double sumSqOriginal = 0, sumSqProcessed = 0, sumSqResidual = 0, peakResidual = 0;
    for (int i = 0; i < originalSamples.Length; i++)
    {
        float difference = originalSamples[i] - processedSamples[i];
        sumSqOriginal += originalSamples[i] * originalSamples[i];
        sumSqProcessed += processedSamples[i] * processedSamples[i];
        sumSqResidual += difference * difference;
        peakResidual = Math.Max(peakResidual, Math.Abs(difference));
    }

    double rmsOriginal = Math.Sqrt(sumSqOriginal / originalSamples.Length);
    double rmsProcessed = Math.Sqrt(sumSqProcessed / processedSamples.Length);
    double rmsResidual = Math.Sqrt(sumSqResidual / originalSamples.Length);
    double snr = rmsResidual > 0 ? 20.0 * Math.Log10(rmsOriginal / rmsResidual) : double.PositiveInfinity;

    double seconds = processedSamples.Length / (double)(processedReader.WaveFormat.SampleRate * processedReader.WaveFormat.Channels);
    double decodedKbits = processedSamples.Length * 32.0 / 1000.0;
    double decodedKbps = decodedKbits / Math.Max(seconds, 1e-9);

    string summaryPath = GetSummaryPath(processedPath);

    using (var summary = new StreamWriter(summaryPath, false))
    {
        summary.WriteLine($"Mode: {mode}");
        summary.WriteLine($"Input file: {Path.GetFileName(originalPath)}");
        summary.WriteLine($"Output file: {Path.GetFileName(processedPath)}");
        if (plannerProcessingSeconds.HasValue)
        {
            summary.WriteLine($"Planner processing seconds: {FormatDouble(plannerProcessingSeconds.Value)}");
            summary.WriteLine($"Planner processing ms: {FormatDouble(plannerProcessingSeconds.Value * 1000.0)}");
        }
        if (engineProcessingSeconds.HasValue)
        {
            summary.WriteLine($"Engine processing seconds: {FormatDouble(engineProcessingSeconds.Value)}");
            summary.WriteLine($"Engine processing ms: {FormatDouble(engineProcessingSeconds.Value * 1000.0)}");
        }
        if (totalProcessingSeconds.HasValue)
        {
            summary.WriteLine($"Total processing seconds: {FormatDouble(totalProcessingSeconds.Value)}");
            summary.WriteLine($"Total processing ms: {FormatDouble(totalProcessingSeconds.Value * 1000.0)}");
        }
        summary.WriteLine($"Duration seconds: {FormatDouble(seconds)}");
        summary.WriteLine($"Decoded WAV kbits: {FormatDouble(decodedKbits)}");
        summary.WriteLine($"Decoded WAV bitrate kbps: {FormatDouble(decodedKbps)}");
        summary.WriteLine($"SNR dB: {FormatDouble(snr)}");
        summary.WriteLine($"RMS original: {FormatDouble(rmsOriginal)}");
        summary.WriteLine($"RMS processed: {FormatDouble(rmsProcessed)}");
        summary.WriteLine($"RMS residual: {FormatDouble(rmsResidual)}");
        summary.WriteLine($"Peak residual: {FormatDouble(peakResidual)}");
    }

    Console.WriteLine($"Summary written: {summaryPath}");
}

static void AppendArchiveSummary(string decodedOutputPath, string archiveOutputPath, int archiveLengthBytes)
{
    string summaryPath = GetSummaryPath(decodedOutputPath);
    double seconds;
    using (var reader = new WaveFileReader(decodedOutputPath))
    {
        seconds = reader.TotalTime.TotalSeconds;
    }

    double archiveKbits = archiveLengthBytes * 8.0 / 1000.0;
    double archiveKbps = seconds > 0 ? archiveKbits / seconds : 0.0;

    byte[] archiveBytes = File.ReadAllBytes(archiveOutputPath);
    var container = new PulsarPacker().Unpack(archiveBytes);
    long audioBytes = 0;
    foreach (var superframe in container.Superframes)
    {
        audioBytes += superframe.EntropyPayload.Length;
    }

    double audioKbits = audioBytes * 8.0 / 1000.0;
    double audioKbps = seconds > 0 ? audioKbits / seconds : 0.0;
    double metadataKbits = archiveKbits - audioKbits;
    if (metadataKbits < 0) metadataKbits = 0;

    using (var writer = new StreamWriter(summaryPath, true))
    {
        writer.WriteLine();
        writer.WriteLine("--- Archive Summary ---");
        writer.WriteLine($"Archive file: {Path.GetFileName(archiveOutputPath)}");
        writer.WriteLine($"Archive size kbits: {FormatDouble(archiveKbits)}");
        writer.WriteLine($"Archive bitrate kbps: {FormatDouble(archiveKbps)}");
        writer.WriteLine($"Audio payload kbits: {FormatDouble(audioKbits)}");
        writer.WriteLine($"Audio payload bitrate kbps: {FormatDouble(audioKbps)}");
        writer.WriteLine($"Metadata overhead kbits: {FormatDouble(metadataKbits)}");
        writer.WriteLine($"Metadata overhead ratio: {FormatDouble(metadataKbits / Math.Max(archiveKbits, 1e-9))}");
    }
}

static void CompareWavs(string originalPath, string processedPath) { /* Omitted for brevity */ }

static string GetSummaryPath(string outputPath)
{
    string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
    return Path.Combine(outputDirectory, "summary.txt");
}

static string FindWorkspaceRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "TestWAVs"))) return current.FullName;
        current = current.Parent;
    }
    return Directory.GetCurrentDirectory();
}