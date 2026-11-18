using System.Globalization;
using System.Text;

namespace MiruPlay.Windows.Services;

/// <summary>Resolved playback time data for an enabled RIFE session.</summary>
public sealed record RifePlaybackPlan(
    string ScriptPath,
    string VSScriptPath,
    string RifePluginPath,
    int Factor,
    string Model,
    int ModelIndex,
    int GpuThread,
    string ProcessingHeight,
    int MaxHeight);

/// <summary>Either a runnable RIFE plan or the reason it must be skipped (degraded to plain mpv).</summary>
public sealed record RifePlaybackRequest(RifePlaybackPlan? Plan, string? DegradedReasonKey);

public static class RifePlaybackScriptBuilder
{
    /// <summary>Video filter script injected into libmpv: Vapoursynth bridge + RIFE.
    /// video_in/container_fps are provided by libmpv's vapoursynth filter.</summary>
    public static string BuildPlaybackScript(RifePlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = new StringBuilder(1024);
        builder.AppendLine("# MiruPlay RIFE playback script (generated; do not edit)");
        builder.AppendLine("import vapoursynth as vs");
        builder.AppendLine("from vapoursynth import core");
        builder.AppendLine("clip = video_in");
        builder.AppendLine("matrix = \"709\"");
        builder.AppendLine("try:");
        builder.AppendLine("    _m = int(clip.get_frame(0).props.get(\"_Matrix\", 1))");
        builder.AppendLine("    if _m in (5, 6):");
        builder.AppendLine("        matrix = \"601\"");
        builder.AppendLine("    elif _m == 9:");
        builder.AppendLine("        matrix = \"2020ncl\"");
        builder.AppendLine("except Exception:");
        builder.AppendLine("    pass");
        builder.AppendLine("max_height = " + plan.MaxHeight.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("if max_height and clip.height > max_height:");
        builder.AppendLine("    width = int(clip.width * max_height / clip.height / 2) * 2");
        builder.AppendLine("    clip = core.resize.Bicubic(clip=clip, width=width, height=max_height, filter_param_a=1/3, filter_param_b=1/3)");
        builder.AppendLine("src_format = clip.format.id");
        builder.AppendLine("clip = core.resize.Bilinear(clip=clip, format=vs.RGBS, matrix_in_s=matrix)");
        builder.AppendLine("clip = core.rife.RIFE(clip=clip, model=" + plan.ModelIndex.ToString(CultureInfo.InvariantCulture) +
                          ", factor_num=" + plan.Factor.ToString(CultureInfo.InvariantCulture) +
                          ", factor_den=1, gpu_id=0, gpu_thread=" + plan.GpuThread.ToString(CultureInfo.InvariantCulture) + ", sc=True)");
        builder.AppendLine("clip = core.resize.Bilinear(clip=clip, format=src_format, matrix_s=matrix)");
        builder.AppendLine("clip.set_output(0)");
        return builder.ToString();
    }

    /// <summary>Synthetic 480p clip used by the VSPipe child-process self-test.</summary>
    public static string BuildSelfTestScript(
        int modelIndex = 23,
        int gpuThread = 2,
        int width = 854,
        int height = 480,
        int frames = 24)
    {
        var builder = new StringBuilder(768);
        builder.AppendLine("# MiruPlay RIFE self-test (generated; do not edit)");
        builder.AppendLine("import vapoursynth as vs");
        builder.AppendLine("from vapoursynth import core");
        builder.AppendLine("clip = core.std.BlankClip(width=" + width.ToString(CultureInfo.InvariantCulture) +
                          ", height=" + height.ToString(CultureInfo.InvariantCulture) +
                          ", fpsnum=24, fpsden=1, length=" + frames.ToString(CultureInfo.InvariantCulture) +
                          ", format=vs.YUV420P8, color=[32, 96, 160])");
        builder.AppendLine("clip = core.text.FrameNum(clip)");
        builder.AppendLine("src_format = clip.format.id");
        builder.AppendLine("clip = core.resize.Bilinear(clip=clip, format=vs.RGBS, matrix_in_s=\"709\")");
        builder.AppendLine("clip = core.rife.RIFE(clip=clip, model=" + modelIndex.ToString(CultureInfo.InvariantCulture) +
                          ", factor_num=2, factor_den=1, gpu_id=0, gpu_thread=" + gpuThread.ToString(CultureInfo.InvariantCulture) + ", sc=False)");
        builder.AppendLine("clip = core.resize.Bilinear(clip=clip, format=src_format, matrix_s=\"709\")");
        builder.AppendLine("clip.set_output(0)");
        return builder.ToString();
    }

    /// <summary>mpv filter argument. Forward slashes are mandatory; backslashes are filter escapes.</summary>
    public static string BuildFilterArgument(string scriptPath) =>
        "vapoursynth=file=" + RifeEnhancementPaths.ToMpvPath(scriptPath);
}
