using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class OutputPathService
{
    public string GetOutputPath(VideoFileInfo source, AppSettings settings, string? scanRoot, IEnumerable<string>? reservedPaths = null)
    {
        var outputDirectory = GetOutputDirectory(source, settings, scanRoot);
        var extension = source.Extension;
        var prefix = ValidateFileNameFragment(settings.OutputPrefix, "压缩文件前缀");
        var suffix = ValidateFileNameFragment(settings.OutputSuffix, "压缩文件后缀");
        var fileName = $"{prefix}{Path.GetFileNameWithoutExtension(source.FileName)}{suffix}{extension}";
        var candidate = CombineFileName(outputDirectory, fileName);
        var sourceAndCandidateSame = string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(source.FullPath), StringComparison.OrdinalIgnoreCase);
        if (sourceAndCandidateSame && settings.OriginalFileAction != OriginalFileAction.Keep && !IsReserved(candidate, reservedPaths))
        {
            return candidate;
        }

        return GetUniquePath(candidate, reservedPaths);
    }

    public string GetOriginalDestinationPath(
        VideoFileInfo source,
        AppSettings settings,
        string? reservedOutputPath = null,
        IEnumerable<string>? reservedPaths = null)
    {
        var directory = settings.OriginalFileAction switch
        {
            OriginalFileAction.MoveToSelectedDirectory when !string.IsNullOrWhiteSpace(settings.OriginalFilesDirectory) => settings.OriginalFilesDirectory,
            OriginalFileAction.MoveToSiblingChildDirectory => Path.Combine(Path.GetDirectoryName(source.FullPath)!, SanitizeDirectoryName(settings.OriginalFilesSubdirectory, "高质量")),
            OriginalFileAction.Keep => throw new InvalidOperationException("当前设置要求保留原文件，不能计算移动目标。"),
            _ => throw new InvalidOperationException("请选择有效的原文件移动目录。")
        };
        var prefix = ValidateFileNameFragment(settings.OriginalPrefix, "原文件前缀");
        var suffix = ValidateFileNameFragment(settings.OriginalSuffix, "原文件后缀");
        var name = $"{prefix}{Path.GetFileNameWithoutExtension(source.FileName)}{suffix}{source.Extension}";
        var allReservedPaths = reservedPaths?.ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(reservedOutputPath))
        {
            allReservedPaths.Add(reservedOutputPath);
        }

        return GetUniquePath(CombineFileName(directory, name), allReservedPaths);
    }

    public static string GetUniquePath(string candidate, params string?[] reservedPaths)
        => GetUniquePath(candidate, (reservedPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>());

    public static string GetUniquePath(string candidate, IEnumerable<string>? reservedPaths)
    {
        var directory = Path.GetDirectoryName(candidate) ?? throw new InvalidOperationException("输出目录无效。");
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        var result = candidate;
        var index = 1;
        while (File.Exists(result) || IsReserved(result, reservedPaths))
        {
            result = Path.Combine(directory, $"{stem} ({index++}){extension}");
        }

        return result;
    }

    private static string GetOutputDirectory(VideoFileInfo source, AppSettings settings, string? scanRoot)
    {
        return settings.OutputLocation switch
        {
            OutputLocationMode.SameDirectory => Path.GetDirectoryName(source.FullPath)!,
            OutputLocationMode.ChildDirectory => Path.Combine(Path.GetDirectoryName(source.FullPath)!, SanitizeDirectoryName(settings.OutputSubdirectory, "Compressed")),
            OutputLocationMode.SelectedDirectory when !string.IsNullOrWhiteSpace(settings.OutputDirectory) => BuildSelectedOutputDirectory(source, settings, scanRoot),
            OutputLocationMode.SelectedDirectory => throw new InvalidOperationException("请选择统一输出目录。"),
            _ => throw new InvalidOperationException("输出位置无效。")
        };
    }

    private static string BuildSelectedOutputDirectory(VideoFileInfo source, AppSettings settings, string? scanRoot)
    {
        if (!settings.PreserveDirectoryStructure || string.IsNullOrWhiteSpace(scanRoot))
        {
            return settings.OutputDirectory;
        }

        var sourceDirectory = Path.GetDirectoryName(source.FullPath)!;
        var relative = Path.GetRelativePath(scanRoot, sourceDirectory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return settings.OutputDirectory;
        }

        return Path.Combine(settings.OutputDirectory, relative);
    }

    private static string SanitizeDirectoryName(string? value, string fallback)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ContainsDirectorySeparator(result) || result is "." or "..")
        {
            throw new InvalidOperationException("子目录名称不能包含路径分隔符或非法字符。");
        }

        return result;
    }

    private static string ValidateFileNameFragment(string? value, string label)
    {
        var result = value ?? string.Empty;
        if (result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ContainsDirectorySeparator(result))
        {
            throw new InvalidOperationException($"{label}不能包含路径分隔符或非法文件名字符。");
        }

        return result;
    }

    private static string CombineFileName(string directory, string fileName)
    {
        var absoluteDirectory = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(absoluteDirectory, fileName));
        var relative = Path.GetRelativePath(absoluteDirectory, candidate);
        if (Path.IsPathRooted(relative) || relative is "." or ".." || ContainsDirectorySeparator(relative))
        {
            throw new InvalidOperationException("生成的文件名必须位于指定输出目录内。");
        }

        return candidate;
    }

    private static bool ContainsDirectorySeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsSamePath(string left, string? right) =>
        !string.IsNullOrWhiteSpace(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsReserved(string candidate, IEnumerable<string>? reservedPaths) =>
        reservedPaths?.Any(path => IsSamePath(candidate, path)) == true;
}
