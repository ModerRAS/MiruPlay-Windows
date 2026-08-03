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
Number	Enabled	Control	Type	Frequency(Hz)	Gain(dB)	Q	Bandwidth(Hz)
1	True	Auto	PK	70.00	-14.7	10.398	6.73
2	False	Auto	PK	71.90	9.0	6.993	10.28
3	True	Manual	LS	78.30	5.7		
4	True	Auto	None				

Compound_filters
Number	Enabled	Control	Type
1	True	Auto	None
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
