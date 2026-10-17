using KaraokeFilmMaker.Application.Timing;

namespace KaraokeFilmMaker.Infrastructure.Audio;

/// <summary>
/// Lightweight onset/phase estimator for a single, stable tempo. Beat phase is not musical downbeat recognition.
/// Always returns a suggestion: sparse/acoustic or syncopated music can need manual half/double-tempo correction.
/// </summary>
public static class ConstantTempoEstimator
{
    private const double MinimumBpm = 50;
    private const double MaximumBpm = 240;
    private const double MinimumSeconds = 8;
    private readonly record struct Onset(double Seconds, double Strength);
    private readonly record struct Candidate(double Bpm, double Offset, double Score, double Coverage, double Match);

    public static TempoAnalysisResult Estimate(ReadOnlySpan<float> monoSamples, int sampleRate, CancellationToken cancellationToken = default)
    {
        if (sampleRate < 1000 || sampleRate > 192000) { throw new ArgumentOutOfRangeException(nameof(sampleRate)); }
        cancellationToken.ThrowIfCancellationRequested();
        int sampleCount = Math.Min(monoSamples.Length, checked(sampleRate * OfflineTempoAnalysisService.MaximumAnalysisSeconds));
        double seconds = (double)sampleCount / sampleRate;
        if (seconds < MinimumSeconds) { return Uncertain("歌曲太短：至少需要 8 秒有节奏的音频，未更改节拍器。"); }

        Onset[] onsets = ExtractOnsets(monoSamples[..sampleCount], sampleRate, cancellationToken);
        if (onsets.Length < 8 || onsets[^1].Seconds - onsets[0].Seconds < 5)
        {
            return Uncertain("前 90 秒内没有足够清晰的拍点（可能是静音、长前奏或弱节奏），请手动设置。");
        }

        var candidates = new List<Candidate>();
        // Fold onset times into each grid, then refine the local maxima. This compares half/double tempo
        // as well as ordinary candidates instead of forcing all songs into a fixed popular BPM range.
        for (double bpm = MinimumBpm; bpm <= MaximumBpm; bpm += 0.5)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(Evaluate(onsets, bpm));
        }

        Candidate[] coarse = candidates.OrderByDescending(item => item.Score)
            .Aggregate(new List<Candidate>(), (selected, item) =>
            {
                if (selected.Count < 10 && selected.All(other => Math.Abs(other.Bpm - item.Bpm) > 2)) { selected.Add(item); }
                return selected;
            }).ToArray();
        var refined = new List<Candidate>();
        foreach (Candidate candidate in coarse)
        {
            Candidate best = candidate;
            for (double bpm = Math.Max(MinimumBpm, candidate.Bpm - 0.6); bpm <= Math.Min(MaximumBpm, candidate.Bpm + 0.6); bpm += 0.01)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Candidate evaluated = Evaluate(onsets, bpm);
                if (evaluated.Score > best.Score) { best = evaluated; }
            }
            refined.Add(RefinePhaseAndPeriod(onsets, best));
        }

        Candidate winner = refined.OrderByDescending(item => item.Score).First();
        double confidence = Math.Clamp(winner.Match * Math.Sqrt(winner.Coverage), 0, 1);
        bool reliable = confidence >= 0.46 && winner.Coverage >= 0.42 && winner.Match >= 0.36;
        double roundedBpm = Math.Round(winner.Bpm, 2, MidpointRounding.AwayFromZero);
        double period = 60 / roundedBpm;
        int offset = (int)Math.Round(PositiveModulo(winner.Offset, period) * 1000, MidpointRounding.AwayFromZero);
        if (offset >= (int)Math.Round(period * 1000)) { offset = 0; }
        double[] alternatives = new[] { roundedBpm / 2, roundedBpm * 2 }
            .Where(value => value >= 20 && value <= 400).Select(value => Math.Round(value, 2)).ToArray();
        string message = reliable
            ? "已估计开头最多 90 秒的固定拍点。BPM 可能存在半拍／倍拍歧义，请应用后试听；拍点对齐不代表小节重拍。"
            : "前 90 秒内的节奏不够稳定或起音不清晰，暂不自动填入。可手动设置 BPM／首拍偏移，或稍后重试。";
        return new TempoAnalysisResult(roundedBpm, offset, confidence, reliable, message, alternatives);
    }

    private static Onset[] ExtractOnsets(ReadOnlySpan<float> samples, int sampleRate, CancellationToken cancellationToken)
    {
        int hop = Math.Max(1, (int)Math.Round(sampleRate * 0.005));
        int frames = samples.Length / hop;
        var energy = new double[frames];
        var lowEnergy = new double[frames];
        double low = 0, totalPower = 0;
        double lowAlpha = 1 - Math.Exp(-2 * Math.PI * 180 / sampleRate);
        for (int frame = 0; frame < frames; frame++)
        {
            if (frame % 128 == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            double sum = 0, lowSum = 0;
            for (int index = frame * hop; index < (frame + 1) * hop; index++)
            {
                float sample = samples[index];
                if (!float.IsFinite(sample)) { continue; }
                low += lowAlpha * (sample - low);
                sum += sample * sample;
                lowSum += low * low;
            }
            energy[frame] = Math.Sqrt(sum / hop);
            lowEnergy[frame] = Math.Sqrt(lowSum / hop);
            totalPower += sum;
        }
        if (totalPower / samples.Length < 1e-9) { return []; }

        var novelty = new double[frames];
        double fullScale = Math.Max(1e-9, energy.Max()), lowScale = Math.Max(1e-9, lowEnergy.Max());
        for (int frame = 1; frame < frames; frame++)
        {
            // Timestamp is the beginning of the energy-rise frame, not its end or the envelope peak centre.
            // This avoids systematically shifting every inferred beat by an extra analysis window.
            novelty[frame] = 0.65 * Math.Max(0, energy[frame] - energy[frame - 1]) / fullScale
                + 0.35 * Math.Max(0, lowEnergy[frame] - lowEnergy[frame - 1]) / lowScale;
        }
        double threshold = Math.Max(0.012, novelty.Average() * 2.2);
        var peaks = new List<Onset>();
        for (int frame = 2; frame < frames - 2; frame++)
        {
            if (novelty[frame] < threshold || novelty[frame] < novelty[frame - 1] || novelty[frame] <= novelty[frame + 1]
                || novelty[frame] < novelty[frame - 2] || novelty[frame] < novelty[frame + 2]) { continue; }
            var onset = new Onset((double)(frame * hop) / sampleRate, novelty[frame]);
            if (peaks.Count > 0 && onset.Seconds - peaks[^1].Seconds < 0.075)
            {
                if (onset.Strength > peaks[^1].Strength) { peaks[^1] = onset; }
            }
            else { peaks.Add(onset); }
        }
        if (peaks.Count == 0) { return []; }
        double cap = peaks.Select(item => item.Strength).Order().ElementAt(peaks.Count / 2) * 3;
        return peaks.Select(item => item with { Strength = Math.Min(item.Strength, cap) }).ToArray();
    }

    private static Candidate Evaluate(Onset[] onsets, double bpm)
    {
        double period = 60 / bpm;
        int binCount = Math.Max(20, (int)Math.Round(period / 0.005));
        double binSeconds = period / binCount;
        var bins = new double[binCount];
        double tolerance = Math.Min(0.035, period * 0.07);
        int radius = Math.Max(1, (int)Math.Ceiling(tolerance / binSeconds));
        foreach (Onset onset in onsets)
        {
            int centre = (int)Math.Round(PositiveModulo(onset.Seconds, period) / binSeconds) % binCount;
            for (int distance = -radius; distance <= radius; distance++)
            {
                int index = (centre + distance + binCount) % binCount;
                bins[index] += onset.Strength * Math.Max(0, 1 - Math.Abs(distance * binSeconds) / tolerance);
            }
        }
        int phase = Array.IndexOf(bins, bins.Max());
        return Score(onsets, bpm, phase * binSeconds);
    }

    private static Candidate Score(Onset[] onsets, double bpm, double offset)
    {
        double period = 60 / bpm;
        double tolerance = Math.Min(0.035, period * 0.07);
        double matched = 0, total = 0;
        var beats = new HashSet<int>();
        foreach (Onset onset in onsets)
        {
            total += onset.Strength;
            int beat = (int)Math.Round((onset.Seconds - offset) / period);
            double residual = Math.Abs(onset.Seconds - (offset + beat * period));
            if (residual < tolerance)
            {
                matched += onset.Strength * Math.Max(0, 1 - residual / tolerance * 0.5);
                beats.Add(beat);
            }
        }
        double coverage = Math.Min(1, beats.Count / Math.Max(1, (onsets[^1].Seconds - onsets[0].Seconds) / period + 1));
        double match = matched / Math.Max(1e-9, total);
        return new Candidate(bpm, offset, match * Math.Sqrt(coverage), coverage, match);
    }

    private static Candidate RefinePhaseAndPeriod(Onset[] onsets, Candidate candidate)
    {
        double period = 60 / candidate.Bpm;
        double tolerance = Math.Min(0.035, period * 0.07);
        var matching = onsets.Select(onset => (Onset: onset, Beat: Math.Round((onset.Seconds - candidate.Offset) / period)))
            .Where(item => Math.Abs(item.Onset.Seconds - candidate.Offset - item.Beat * period) < tolerance).ToArray();
        if (matching.Length < 4) { return candidate; }
        double weight = matching.Sum(item => item.Onset.Strength);
        double meanBeat = matching.Sum(item => item.Beat * item.Onset.Strength) / weight;
        double meanTime = matching.Sum(item => item.Onset.Seconds * item.Onset.Strength) / weight;
        double denominator = matching.Sum(item => item.Onset.Strength * Math.Pow(item.Beat - meanBeat, 2));
        if (denominator < 1e-9) { return candidate; }
        double fittedPeriod = matching.Sum(item => item.Onset.Strength * (item.Beat - meanBeat) * (item.Onset.Seconds - meanTime)) / denominator;
        double fittedBpm = 60 / fittedPeriod;
        if (fittedBpm < MinimumBpm || fittedBpm > MaximumBpm || Math.Abs(fittedBpm - candidate.Bpm) > 1) { return candidate; }
        return Score(onsets, fittedBpm, PositiveModulo(meanTime - fittedPeriod * meanBeat, fittedPeriod));
    }

    private static double PositiveModulo(double value, double modulus) => (value % modulus + modulus) % modulus;
    private static TempoAnalysisResult Uncertain(string message) => new(null, null, 0, false, message, []);
}
