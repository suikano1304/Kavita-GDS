using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Kavita.Models.Entities;

namespace Kavita.Services.Helpers;

public static class DownloadFileName
{
    public static string Clean(string? value)
    {
        var clean = Regex.Replace(value ?? "", "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(clean)) clean = "기타";
        if (Regex.IsMatch(clean.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase)) clean = "_" + clean;
        return clean.Length > 160 ? clean[..160].TrimEnd('.', ' ') : clean;
    }

    public static string VolumeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0") return "";
        var label = value.Trim();
        if (decimal.TryParse(label, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && number >= 0)
            label = number < 10 ? "0" + number.ToString("0.################", CultureInfo.InvariantCulture) : number.ToString("0.################", CultureInfo.InvariantCulture);
        return label.EndsWith("권", StringComparison.Ordinal) ? label : label + "권";
    }

    public static string BookLabel(Chapter chapter, Volume volume, int ordinal)
    {
        if (chapter.IsSpecial)
            return new[] { chapter.TitleName, chapter.Title, chapter.Range }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? $"기타_{ordinal:D2}";
        var label = VolumeLabel(volume.Name);
        return label.Length > 0 ? label : new[] { chapter.TitleName, chapter.Title, chapter.Range }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "0") ?? $"기타_{ordinal:D2}";
    }

    public static string Unique(string stem, string extension, ISet<string> used)
    {
        stem = Clean(stem);
        var candidate = stem + extension;
        for (var suffix = 2; !used.Add(candidate); suffix++) candidate = $"{stem}_{suffix:D2}{extension}";
        return candidate;
    }
}
