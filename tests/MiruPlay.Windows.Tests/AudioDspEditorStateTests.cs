using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AudioDspEditorStateTests
{
    [Fact]
    public void ImportRewReplacesOnlyTheSelectedChannelRule()
    {
        var config = new AudioDspConfig(true, "p", [new(
            "p",
            "Preset",
            Rules: [
                new(AudioDspChannelTarget.Left, [new(GainDb: 2)]),
                new(AudioDspChannelTarget.Right, [new(GainDb: 3)]),
            ])]);
        var imported = new[] { new AudioDspBand(GainDb: -14.7, FrequencyHz: 70, Q: 10.398) };

        var updated = AudioDspEditorState.ReplaceChannelBands(
            config, "p", AudioDspChannelTarget.Left, imported);

        Assert.Equal(-14.7, updated.Presets![0].Rules![0].Bands![0].GainDb, 3);
        Assert.Equal(3, updated.Presets![0].Rules![1].Bands![0].GainDb);
    }
}
