using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Services.Scanner;

namespace Kavita.Services.Helpers;

public static class GdsScanFingerprintHelper
{
    public const int FingerprintVersion = 1;

    private static readonly string[] DirectorySidecars =
    [
        "kavita.yaml",
        "kavita.yml",
        ".special",
        "cover.jpg",
        "cover.jpeg",
        "cover.png",
        "cover.webp",
        "folder.jpg",
        "folder.jpeg",
        "folder.png",
        "folder.webp",
        "poster.jpg",
        "poster.jpeg",
        "poster.png",
        "poster.webp",
    ];

    public static GdsScanFingerprintState Calculate(IEnumerable<ParserInfo> parserInfos, bool includeSidecars = true)
    {
        var fileLines = new List<string>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var specialDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentLastModifiedUtc = DateTime.MinValue;

        foreach (var info in parserInfos.Where(p => !string.IsNullOrWhiteSpace(p.FullFilePath)))
        {
            var normalizedPath = Parser.NormalizePath(info.FullFilePath);
            var directory = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directories.Add(directory);
                AddAncestorDirectories(directory, specialDirectories);
            }

            var extension = Path.GetExtension(normalizedPath).ToLowerInvariant();
            try
            {
                var fileInfo = new FileInfo(normalizedPath);
                if (!fileInfo.Exists)
                {
                    fileLines.Add($"file|missing|{normalizedPath}|{(int) info.Format}|{extension}");
                    continue;
                }

                var lastWriteUtc = fileInfo.LastWriteTimeUtc;
                var creationUtc = fileInfo.CreationTimeUtc;
                var fileContentModifiedUtc = Max(lastWriteUtc, creationUtc);
                if (fileContentModifiedUtc > contentLastModifiedUtc)
                {
                    contentLastModifiedUtc = fileContentModifiedUtc;
                }

                fileLines.Add(string.Join('|',
                    "file",
                    normalizedPath,
                    (int) info.Format,
                    extension,
                    fileInfo.Length.ToString(),
                    lastWriteUtc.Ticks.ToString(),
                    creationUtc.Ticks.ToString()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                fileLines.Add($"file|error|{normalizedPath}|{(int) info.Format}|{extension}|{ex.GetType().Name}");
            }
        }

        var sidecarLines = includeSidecars
            ? directories
                .SelectMany(GetSidecarLines)
                .Concat(specialDirectories.Select(directory => GetFileStateLine("sidecar", Path.Join(directory, ".special"))))
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList()
            : [];

        var fingerprint = ComputeHash(fileLines
            .OrderBy(line => line, StringComparer.Ordinal)
            .Concat(sidecarLines));

        return new GdsScanFingerprintState(fingerprint, contentLastModifiedUtc);
    }

    public static string BuildKey(string normalizedName, MangaFormat format)
    {
        // GDS series can intentionally contain mixed formats. The representative Series.Format can differ
        // from scan to scan depending on which file is parsed first, so fingerprint lookup must be name-based.
        return normalizedName;
    }

    public static bool HasAnySidecars(IEnumerable<ParserInfo> parserInfos)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var specialDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in parserInfos.Where(p => !string.IsNullOrWhiteSpace(p.FullFilePath)))
        {
            var directory = Path.GetDirectoryName(Parser.NormalizePath(info.FullFilePath));
            if (string.IsNullOrWhiteSpace(directory)) continue;

            directories.Add(directory);
            AddAncestorDirectories(directory, specialDirectories);
        }

        return directories.Any(HasDirectorySidecar) ||
               specialDirectories.Any(directory => File.Exists(Path.Join(directory, ".special")));
    }

    private static IEnumerable<string> GetSidecarLines(string directory)
    {
        foreach (var fileName in DirectorySidecars)
        {
            var path = Path.Join(directory, fileName);
            yield return GetFileStateLine("sidecar", path);
        }
    }

    private static void AddAncestorDirectories(string directory, ISet<string> directories)
    {
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            directories.Add(current.FullName);
            current = current.Parent;
        }
    }

    private static string GetFileStateLine(string prefix, string path)
    {
        var normalizedPath = Parser.NormalizePath(path);
        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists) return $"{prefix}|missing|{normalizedPath}";

            return string.Join('|',
                prefix,
                normalizedPath,
                fileInfo.Length.ToString(),
                fileInfo.LastWriteTimeUtc.Ticks.ToString(),
                fileInfo.CreationTimeUtc.Ticks.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{prefix}|error|{normalizedPath}|{ex.GetType().Name}";
        }
    }

    private static bool HasDirectorySidecar(string directory)
    {
        foreach (var fileName in DirectorySidecars)
        {
            if (File.Exists(Path.Join(directory, fileName))) return true;
        }

        return false;
    }

    private static string ComputeHash(IEnumerable<string> lines)
    {
        using var sha = SHA256.Create();
        var payload = string.Join('\n', lines.Prepend($"version|{FingerprintVersion}"));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static DateTime Max(DateTime left, DateTime right)
    {
        return left >= right ? left : right;
    }
}

public sealed record GdsScanFingerprintState(string Fingerprint, DateTime ContentLastModifiedUtc);
