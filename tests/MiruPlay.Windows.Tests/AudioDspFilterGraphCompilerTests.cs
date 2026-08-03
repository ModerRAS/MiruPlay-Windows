using System.Text.RegularExpressions;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AudioDspFilterGraphCompilerTests
{
    [Fact]
    public void DisabledConfigProducesNoAudioDspArguments()
    {
        var graph = AudioDspFilterGraphCompiler.Compile(
            AudioDspConfig.Neutral(), AudioDspChannelLayout.Stereo, 48_000);

        Assert.Empty(graph.MpvArguments);
        Assert.Equal("disabled", graph.EffectiveRoute);
    }

    [Fact]
    public void LinearStereoGraphContainsIndependentFirequalizerBranchesAndSharedDelay()
    {
        var config = new AudioDspConfig(
            true,
            "stereo",
            [new(
                "stereo",
                "Stereo",
                PhaseMode: AudioDspPhaseMode.Linear,
                Rules: [new(AudioDspChannelTarget.Left, [new(GainDb: -6)])])]);
        var graph = AudioDspFilterGraphCompiler.Compile(
            config, AudioDspChannelLayout.Stereo, 48_000);

        Assert.Contains("channelsplit", graph.AfValue, StringComparison.Ordinal);
        Assert.Equal(2, graph.AfValue.Split("firequalizer", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, Regex.Count(graph.AfValue, "delay="));
        Assert.Contains("--audio-format=float", graph.MpvArguments);
        Assert.Contains("--audio-spdif=no", graph.MpvArguments);
    }
}
