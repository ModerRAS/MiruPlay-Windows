using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RewEqFileParserTests
{
    [Fact]
    public void ParseReadsGenericColumnsByNameAndSkipsDisabledAndNoneRows()
    {
        const string text = """
Generic
Number\tEnabled\tControl\tType\tFrequency(Hz)\tGain(dB)\tQ\tBandwidth(Hz)
1\tTrue\tAuto\tPK\t70.00\t-14.7\t10.398\t6.73
2\tFalse\tAuto\tPK\t71.90\t9.0\t6.993\t10.28
3\tTrue\tManual\tLS\t78.30\t5.7
4\tTrue\tAuto\tNone

Compound_filters
Number\tEnabled\tControl\tType
1\tTrue\tAuto\tNone
""";

        var result = RewEqFileParser.Parse(text);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Bands.Count);
        Assert.Equal(AudioDspFilterType.Peaking, result.Bands[0].Band.Type);
        Assert.Equal(70, result.Bands[0].Band.FrequencyHz);
        Assert.Equal(AudioDspFilterType.LowShelf, result.Bands[1].Band.Type);
    }

    [Fact]
    public void ParseReportsLineNumberForUnsupportedEnabledFilter()
    {
        var result = RewEqFileParser.Parse("Generic\nType\tEnabled\nX\tTrue\n");

        Assert.Contains(result.Errors, error => error.LineNumber == 3);
    }
}
