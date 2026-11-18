namespace MiruPlay.Windows.Tv;

public enum TvGamepadFaceLayout
{
    Xbox,
    Nintendo,
}

public static class TvGamepadFaceLayouts
{
    public static IReadOnlyList<string> Ids { get; } = ["xbox", "nintendo"];

    public static string Normalize(string? value) =>
        string.Equals(value, "nintendo", StringComparison.OrdinalIgnoreCase) ? "nintendo" : "xbox";

    public static TvGamepadFaceLayout Parse(string? value) =>
        Normalize(value) == "nintendo" ? TvGamepadFaceLayout.Nintendo : TvGamepadFaceLayout.Xbox;
}