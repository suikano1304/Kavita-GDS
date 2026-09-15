namespace Kavita.Models.DTOs.Archive;

public sealed record DownloadArchiveEntry(string SourcePath, string EntryName, bool RequireStableSource = true);
