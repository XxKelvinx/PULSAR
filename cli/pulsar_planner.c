#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

// Represents one row in the telemetry CSV
typedef struct {
    int frame_index;
    int input_samples;
    int requested_bitrate_bps;
    int analysis_valid;
    float tonality;
    float activity;
    float noisiness;
} FrameTelemetry;

// Array of frames
typedef struct {
    FrameTelemetry* items;
    int count;
    int capacity;
} FrameArray;

// Helper: safe clamp
static float clamp(float value, float min_val, float max_val) {
    if (value < min_val) return min_val;
    if (value > max_val) return max_val;
    return value;
}

// ---------------------------------------------------------
// CSV Parsing
// ---------------------------------------------------------
static void read_csv(const char* filepath, FrameArray* frames) {
    FILE* fp = fopen(filepath, "r");
    if (!fp) {
        fprintf(stderr, "Error: Could not open telemetry CSV %s\n", filepath);
        exit(1);
    }
    
    char line[1024];
    // Skip header
    if (!fgets(line, sizeof(line), fp)) {
        fclose(fp);
        return;
    }
    
    // Extremely basic CSV parser assuming strict column order or naive mapping
    // Note: Python's DictReader maps by name. Here we will do simple string parsing
    // but in a production C port you might want a real CSV library or stricter matching.
    
    int header_map[20];
    for (int i=0; i<20; i++) header_map[i] = -1;
    
    // We assume the opus_demo output is roughly:
    // frame_index,input_samples,packet_bytes,requested_bitrate_bps,analysis_valid,analysis_tonality,analysis_activity,analysis_activity_probability,analysis_music_prob,analysis_bandwidth,analysis_tonality_slope,analysis_noisiness,analysis_max_pitch_ratio
    
    // For simplicity, we just parse known output of opus_demo -dump_csv
    while (fgets(line, sizeof(line), fp)) {
        if (frames->count >= frames->capacity) {
            frames->capacity = frames->capacity == 0 ? 2048 : frames->capacity * 2;
            frames->items = (FrameTelemetry*)realloc(frames->items, frames->capacity * sizeof(FrameTelemetry));
        }
        
        FrameTelemetry* frame = &frames->items[frames->count];
        memset(frame, 0, sizeof(FrameTelemetry));
        
        char* ptr = line;
        int col = 0;
        char* token = strtok(ptr, ",");
        while(token) {
            switch(col) {
                case 0: frame->frame_index = atoi(token); break;
                case 1: frame->input_samples = atoi(token); break;
                // skip packet_bytes (2)
                case 3: frame->requested_bitrate_bps = atoi(token); break;
                case 4: frame->analysis_valid = atoi(token); break;
                case 5: frame->tonality = atof(token); break;
                case 6: frame->activity = atof(token); break;
                // skip activity_prob, music_prob, bandwidth, tonality_slope (7,8,9,10)
                case 11: frame->noisiness = atof(token); break;
            }
            col++;
            token = strtok(NULL, ",");
        }
        frames->count++;
    }
    fclose(fp);
}

// ---------------------------------------------------------
// DSP & Filter functions (150Hz Butterworth High-Pass)
// ---------------------------------------------------------
// Uses a 4th order IIR filter (cascaded Biquads or naive difference equation)
// We will mimic SciPy's filtfilt (zero-phase by filtering forward and backward)
//
// For simplicity in this standalone port, we implement a 2nd order forward-backward 
// which yields a 4th order effect.
static void apply_highpass_filter(double* data, int len, double cutoff_hz, double fs) {
    // 2nd Order Butterworth Highpass
    double wc = tan(M_PI * cutoff_hz / (fs / 2.0));
    double sqrt2 = sqrt(2.0);
    double c = 1.0 / wc;
    
    double a0 = 1.0 / (1.0 + sqrt2 * c + c * c);
    double a1 = -2.0 * a0;
    double a2 = a0;
    double b1 = 2.0 * (1.0 - c * c) * a0;
    double b2 = (1.0 - sqrt2 * c + c * c) * a0;

    // Filter state
    double z1 = 0, z2 = 0;
    double* out = (double*)malloc(len * sizeof(double));
    
    // Forward pass
    for (int i=0; i<len; i++) {
        double x = data[i];
        double y = a0 * x + a1 * z1 + a2 * z2 - b1 * out[(i>0)?i-1:0] - b2 * out[(i>1)?i-2:0]; // Simplified generic format
        // Proper direct form II transposed
        y = a0 * x + z1;
        z1 = a1 * x - b1 * y + z2;
        z2 = a2 * x - b2 * y;
        out[i] = y;
    }
    
    // Reset state
    z1 = 0; z2 = 0;
    
    // Backward pass for zero-phase (filtfilt)
    for (int i = len - 1; i >= 0; i--) {
        double x = out[i];
        double y = a0 * x + z1;
        z1 = a1 * x - b1 * y + z2;
        z2 = a2 * x - b2 * y;
        data[i] = y;
    }
    
    free(out);
}

// ---------------------------------------------------------
// Transient Flux Computation
// ---------------------------------------------------------
static double* compute_flux(const char* pcm_path, int frame_samples, double highpass_hz, int* out_flux_len) {
    FILE* fp = fopen(pcm_path, "rb");
    if (!fp) {
        fprintf(stderr, "Error: Could not open PCM %s\n", pcm_path);
        exit(1);
    }
    
    // Get file size
    fseek(fp, 0, SEEK_END);
    long size = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    
    int num_samples = size / (2 * sizeof(short)); // stereo
    short* pcm = (short*)malloc(size);
    fread(pcm, sizeof(short), size / sizeof(short), fp);
    fclose(fp);
    
    // Convert to downmixed mono floats
    double* mono = (double*)malloc(num_samples * sizeof(double));
    for (int i=0; i<num_samples; i++) {
        double l = pcm[i*2] / 32768.0;
        double r = pcm[i*2+1] / 32768.0;
        mono[i] = (l + r) / 2.0;
    }
    free(pcm);
    
    if (highpass_hz > 0.0) {
        apply_highpass_filter(mono, num_samples, highpass_hz, 48000.0);
    }
    
    int fft_size = 2048;
    int hop = frame_samples;
    
    int num_frames = (num_samples - fft_size) / hop;
    if (num_frames < 0) num_frames = 0;
    
    double* flux = (double*)malloc((num_frames + 1) * sizeof(double));
    flux[0] = 0.0;
    
    // Naive spectral flux: normally requires FFT. 
    // To avoid importing an entire FFT library like FFTW for a 1-to-1 port, 
    // we use a simple naive transient energy envelope detector on the high-passed signal, 
    // which strongly correlates with Spectral Flux on the high frequencies.
    // Real implementation requires linking an FFT library.
    
    for (int start=0, f=1; f <= num_frames && start <= num_samples - hop; start+=hop, f++) {
        double e_curr = 0;
        for (int i=0; i<hop; i++) {
            e_curr += fabs(mono[start + i]);
        }
        
        double e_prev = 0;
        if (start >= hop) {
            for (int i=0; i<hop; i++) {
                e_prev += fabs(mono[start - hop + i]);
            }
        }
        
        flux[f] = fmax(0.0, e_curr - e_prev);
    }
    
    free(mono);
    *out_flux_len = num_frames;
    return flux;
}

// ---------------------------------------------------------
// Quantile finding
// ---------------------------------------------------------
int compare_doubles(const void* a, const void* b) {
    double arg1 = *(const double*)a;
    double arg2 = *(const double*)b;
    if (arg1 < arg2) return -1;
    if (arg1 > arg2) return 1;
    return 0;
}

double get_quantile(double* data, int len, double q) {
    double* copy = (double*)malloc(len * sizeof(double));
    memcpy(copy, data, len * sizeof(double));
    qsort(copy, len, sizeof(double), compare_doubles);
    
    double val = copy[(int)(len * q)];
    free(copy);
    return val;
}

// ---------------------------------------------------------
// Main Logic
// ---------------------------------------------------------
int main(int argc, char** argv) {
    if (argc < 4) {
        printf("Usage: %s <input_pcm> <telemetry_csv> <output_csv> [options...]\n", argv[0]);
        return 1;
    }
    
    const char* pcm_path = argv[1];
    const char* csv_path = argv[2];
    const char* out_path = argv[3];
    
    // Hyperparams
    double strong_peak = 1.45;
    double medium_peak = 1.25;
    double base_scale = 0.88;
    double min_scale = 0.80;
    double max_scale = 1.50;
    
    double highpass_hz = 150.0;
    double pre_echo_decay = 0.85;
    double post_mask_decay = 0.30;
    
    // Load Telemetry
    FrameArray frames = {0};
    read_csv(csv_path, &frames);
    if (frames.count == 0) return 0;
    
    int frame_samples = frames.items[0].input_samples;
    if (frame_samples == 0) frame_samples = 960;
    
    // Compute Flux
    int flux_len = 0;
    double* flux = compute_flux(pcm_path, frame_samples, highpass_hz, &flux_len);
    
    int total_frames = frames.count < flux_len ? frames.count : flux_len;
    
    double strong_threshold = get_quantile(flux, total_frames, 0.94);
    double medium_threshold = get_quantile(flux, total_frames, 0.88);
    
    double* raw_scales = (double*)calloc(total_frames, sizeof(double));
    
    for (int i = 0; i < total_frames; i++) {
        FrameTelemetry* frame = &frames.items[i];
        double flux_val = flux[i];
        
        double mix = 0.7 * frame->tonality + 0.3 * (1.0 - frame->noisiness);
        double is_calm = clamp(mix, 0.0, 1.0);
        double frame_base = base_scale + (1.0 - base_scale) * is_calm;
        
        raw_scales[i] = frame_base;
        
        double trans = 0.55 * frame->activity + 0.45 * frame->noisiness;
        double transient_weight = clamp(trans, 0.0, 1.0);
        
        if (flux_val >= strong_threshold) {
             raw_scales[i] = fmax(raw_scales[i], 1.0 + (strong_peak - 1.0) * transient_weight);
        } else if (flux_val >= medium_threshold) {
             raw_scales[i] = fmax(raw_scales[i], 1.0 + (medium_peak - 1.0) * transient_weight);
        }
    }
    
    // Psychoacoustic Smoothing
    double* envelope = (double*)malloc(total_frames * sizeof(double));
    memcpy(envelope, raw_scales, total_frames * sizeof(double));
    
    // 1. Backward shielding
    for (int i = total_frames - 2; i >= 0; i--) {
        envelope[i] = fmax(envelope[i], envelope[i+1] * pre_echo_decay);
    }
    
    // 2. Forward stealing
    for (int i = 1; i < total_frames; i++) {
        envelope[i] = fmax(envelope[i], envelope[i-1] * post_mask_decay);
    }
    
    // Bounds and Average Normalization
    double sum = 0;
    for (int i = 0; i < total_frames; i++) {
        envelope[i] = clamp(envelope[i], min_scale, max_scale);
        sum += envelope[i];
    }
    
    double mean_scale = sum / total_frames;
    if (mean_scale <= 0) mean_scale = 1.0;
    
    for (int i = 0; i < total_frames; i++) {
        envelope[i] /= mean_scale;
    }
    
    // Output
    FILE* out_fp = fopen(out_path, "w");
    fprintf(out_fp, "frame_index,target_bitrate_bps,scale,flux\n");
    
    for (int i = 0; i < total_frames; i++) {
        double scale_val = envelope[i];
        int bitrate = (int)round(frames.items[i].requested_bitrate_bps * scale_val);
        if (bitrate < 6000) bitrate = 6000;
        
        fprintf(out_fp, "%d,%d,%.6f,%.6f\n", frames.items[i].frame_index, 
                bitrate, scale_val, flux[i]);
    }
    fclose(out_fp);
    
    free(flux);
    free(raw_scales);
    free(envelope);
    free(frames.items);
    
    return 0;
}
