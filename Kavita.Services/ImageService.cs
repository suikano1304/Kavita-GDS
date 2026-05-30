using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Entities.Interfaces;
using Kavita.Models.Extensions;
using Kavita.Services.Scanner;
using Microsoft.Extensions.Logging;
using NetVips;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using Image = NetVips.Image;

namespace Kavita.Services;

public class ImageService(ILogger<ImageService> logger, IDirectoryService directoryService)
    : IImageService
{
    public const string Name = "ImageService";

    public const string ChapterCoverImageRegex = @"v\d+_c\d+";
    public const string SeriesCoverImageRegex = @"series\d+";
    public const string CollectionTagCoverImageRegex = @"tag\d+";
    public const string ReadingListCoverImageRegex = @"readinglist\d+";
    public const string PersonCoverImageRegex = @"person\d+";

    private const double WhiteThreshold = 0.95; // Colors with lightness above this are considered too close to white
    private const double BlackThreshold = 0.25; // Colors with lightness below this are considered too close to black


    /// <summary>
    /// Width of the Thumbnail generation
    /// </summary>
    private const int ThumbnailWidth = 320;
    /// <summary>
    /// Height of the Thumbnail generation
    /// </summary>
    private const int ThumbnailHeight = 455;
    /// <summary>
    /// Width of a cover for Library
    /// </summary>
    public const int LibraryThumbnailWidth = 32;


    public void ExtractImages(string? fileFilePath, string targetDirectory, int fileCount = 1)
    {
        if (string.IsNullOrEmpty(fileFilePath)) return;
        directoryService.ExistOrCreate(targetDirectory);
        if (fileCount == 1)
        {
            directoryService.CopyFileToDirectory(fileFilePath, targetDirectory);
        }
        else
        {
            directoryService.CopyDirectoryToDirectory(directoryService.FileSystem.Path.GetDirectoryName(fileFilePath), targetDirectory,
                Parser.ImageFileExtensions);
        }
    }

    /// <summary>
    /// Tries to determine if there is a better mode for resizing
    /// </summary>
    /// <param name="image"></param>
    /// <param name="targetWidth"></param>
    /// <param name="targetHeight"></param>
    /// <returns></returns>
    public static Enums.Size GetSizeForDimensions(Image image, int targetWidth, int targetHeight)
    {
        try
        {
            if (WillScaleWell(image, targetWidth, targetHeight) || IsLikelyWideImage(image.Width, image.Height))
            {
                return Enums.Size.Force;
            }
        }
        catch (Exception)
        {
            /* Swallow */
        }

        return Enums.Size.Both;
    }

    public static Enums.Interesting? GetCropForDimensions(Image image, int targetWidth, int targetHeight)
    {
        try
        {
            if (WillScaleWell(image, targetWidth, targetHeight) || IsLikelyWideImage(image.Width, image.Height))
            {
                return null;
            }
        } catch (Exception)
        {
            /* Swallow */
            return null;
        }

        return Enums.Interesting.Attention;
    }

    public static bool WillScaleWell(Image sourceImage, int targetWidth, int targetHeight, double tolerance = 0.1)
    {
        // Calculate the aspect ratios
        var sourceAspectRatio = (double) sourceImage.Width / sourceImage.Height;
        var targetAspectRatio = (double) targetWidth / targetHeight;

        // Compare aspect ratios
        if (Math.Abs(sourceAspectRatio - targetAspectRatio) > tolerance)
        {
            return false; // Aspect ratios differ significantly
        }

        // Calculate scaling factors
        var widthScaleFactor = (double) targetWidth / sourceImage.Width;
        var heightScaleFactor = (double) targetHeight / sourceImage.Height;

        // Check resolution quality (example thresholds)
        if (widthScaleFactor > 2.0 || heightScaleFactor > 2.0)
        {
            return false; // Scaling factor too large
        }

        return true; // Image will scale well
    }

    private static bool IsLikelyWideImage(int width, int height)
    {
        var aspectRatio = (double) width / height;
        return aspectRatio > 1.25;
    }

    public string GetCoverImage(string path, string fileName, string outputDirectory, EncodeFormat encodeFormat, CoverImageSize size)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        try
        {
            var (width, height) = size.GetDimensions();
            using var sourceImage = Image.NewFromFile(path, false, Enums.Access.SequentialUnbuffered);

            using var thumbnail = Image.Thumbnail(path, width, height: height,
                size: GetSizeForDimensions(sourceImage, width, height),
                crop: GetCropForDimensions(sourceImage, width, height));
            var filename = fileName + encodeFormat.GetExtension();
            thumbnail.WriteToFile(directoryService.FileSystem.Path.Join(outputDirectory, filename));
            return filename;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GetCoverImage] There was an error and prevented thumbnail generation on {ImageFile}. Defaulting to no cover image", path);
        }

        return string.Empty;
    }

    /// <summary>
    /// Creates a thumbnail out of a memory stream and saves to <see cref="DirectoryService.CoverImageDirectory"/> with the passed
    /// fileName and the appropriate extension.
    /// </summary>
    /// <param name="stream">Stream to write to disk. Ensure this is rewinded.</param>
    /// <param name="fileName">filename to save as without extension</param>
    /// <param name="outputDirectory">Where to output the file, defaults to covers directory</param>
    /// <param name="encodeFormat">Export the file as the passed encoding</param>
    /// <returns>File name with extension of the file. This will always write to <see cref="DirectoryService.CoverImageDirectory"/></returns>
    public string WriteCoverThumbnail(Stream stream, string fileName, string outputDirectory, EncodeFormat encodeFormat, CoverImageSize size = CoverImageSize.Default)
    {
        var (targetWidth, targetHeight) = size.GetDimensions();
        if (stream.CanSeek) stream.Position = 0;
        using var sourceImage = Image.NewFromStream(stream);

        var scalingSize = GetSizeForDimensions(sourceImage, targetWidth, targetHeight);
        var scalingCrop = GetCropForDimensions(sourceImage, targetWidth, targetHeight);

        using var thumbnail = sourceImage.ThumbnailImage(targetWidth, targetHeight,
            size: scalingSize,
            crop: scalingCrop);

        var filename = fileName + encodeFormat.GetExtension();
        directoryService.ExistOrCreate(outputDirectory);

        try
        {
            directoryService.FileSystem.File.Delete(directoryService.FileSystem.Path.Join(outputDirectory, filename));
        } catch (Exception) {/* Swallow exception */}

        try
        {
            thumbnail.WriteToFile(directoryService.FileSystem.Path.Join(outputDirectory, filename));

            return filename;
        }
        catch (VipsException)
        {
            // NetVips Issue: https://github.com/kleisauke/net-vips/issues/234
            // Saving pdf covers from a stream can fail, so revert to old code

            if (stream.CanSeek) stream.Position = 0;
            using var thumbnail2 = Image.ThumbnailStream(stream, targetWidth, height: targetHeight,
                size: scalingSize,
                crop: scalingCrop);
            thumbnail2.WriteToFile(directoryService.FileSystem.Path.Join(outputDirectory, filename));

            return filename;
        }
    }

    public string WriteCoverThumbnail(string sourceFile, string fileName, string outputDirectory, EncodeFormat encodeFormat, CoverImageSize size = CoverImageSize.Default)
    {
        var (width, height) = size.GetDimensions();
        using var sourceImage = Image.NewFromFile(sourceFile, false, Enums.Access.SequentialUnbuffered);

        using var thumbnail = Image.Thumbnail(sourceFile, width, height: height,
            size: GetSizeForDimensions(sourceImage, width, height),
            crop: GetCropForDimensions(sourceImage, width, height));
        var filename = fileName + encodeFormat.GetExtension();
        directoryService.ExistOrCreate(outputDirectory);
        try
        {
            directoryService.FileSystem.File.Delete(directoryService.FileSystem.Path.Join(outputDirectory, filename));
        } catch (Exception) {/* Swallow exception */}
        thumbnail.WriteToFile(directoryService.FileSystem.Path.Join(outputDirectory, filename));
        return filename;
    }

    public Task<string> ConvertToEncodingFormat(string filePath, string outputPath, EncodeFormat encodeFormat,
        CancellationToken ct = default)
    {
        var file = directoryService.FileSystem.FileInfo.New(filePath);
        var fileName = file.Name.Replace(file.Extension, string.Empty);
        var outputFile = Path.Join(outputPath, fileName + encodeFormat.GetExtension());

        using var sourceImage = Image.NewFromFile(filePath, false, Enums.Access.SequentialUnbuffered);
        sourceImage.WriteToFile(outputFile);
        return Task.FromResult(outputFile);
    }

    public async Task<bool> IsImage(string filePath, CancellationToken ct = default)
    {
        try
        {
            var info = await SixLabors.ImageSharp.Image.IdentifyAsync(filePath, ct);
            if (info == null) return false;

            return true;
        }
        catch (Exception)
        {
            /* Swallow Exception */
        }

        return false;
    }



    private static (Vector3?, Vector3?) GetPrimarySecondaryColors(string imagePath)
    {
        using var image = Image.NewFromFile(imagePath);
        // Resize the image to speed up processing
        var resizedImage = image.Resize(0.1);

        var processedImage = PreProcessImage(resizedImage);


        // Convert image to RGB array
        var pixels = processedImage.WriteToMemory().ToArray();

        // Convert to list of Vector3 (RGB)
        var rgbPixels = new List<Vector3>();
        for (var i = 0; i < pixels.Length - 2; i += 3)
        {
            rgbPixels.Add(new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]));
        }

        // Perform k-means clustering
        var clusters = KMeansClustering(rgbPixels, 4);

        var sorted = SortByVibrancy(clusters);

        // Ensure white and black are not selected as primary/secondary colors
        sorted = sorted.Where(c => !IsCloseToWhiteOrBlack(c)).ToList();

        if (sorted.Count >= 2)
        {
            return (sorted[0], sorted[1]);
        }
        if (sorted.Count == 1)
        {
            return (sorted[0], null);
        }

        return (null, null);
    }

    private static (Vector3?, Vector3?) GetPrimaryColorSharp(string imagePath)
    {
        using var image = SixLabors.ImageSharp.Image.Load<Rgb24>(imagePath);

        image.Mutate(
            x => x
                // Scale the image down preserving the aspect ratio. This will speed up quantization.
                // We use nearest neighbor as it will be the fastest approach.
                .Resize(new ResizeOptions() { Sampler = KnownResamplers.NearestNeighbor, Size = new SixLabors.ImageSharp.Size(100, 0) })

                // Reduce the color palette to 1 color without dithering.
                .Quantize(new OctreeQuantizer(new QuantizerOptions { MaxColors = 4 })));

        Rgb24 dominantColor = image[0, 0];

        // This will give you a dominant color in HEX format i.e #5E35B1FF
        return (new Vector3(dominantColor.R, dominantColor.G, dominantColor.B), new Vector3(dominantColor.R, dominantColor.G, dominantColor.B));
    }

    private static Image PreProcessImage(Image image)
    {
        return image;
        // Create a mask for white and black pixels
        var whiteMask = image.Colourspace(Enums.Interpretation.Lab)[0] > (WhiteThreshold * 100);
        var blackMask = image.Colourspace(Enums.Interpretation.Lab)[0] < (BlackThreshold * 100);

        // Create a replacement color (e.g., medium gray)
        var replacementColor = new[] { 240.0, 240.0, 240.0 };

        // Apply the masks to replace white and black pixels
        var processedImage = image.Copy();
        processedImage = processedImage.Ifthenelse(whiteMask, replacementColor);
        //processedImage = processedImage.Ifthenelse(blackMask, replacementColor);

        return processedImage;
    }

    private static Dictionary<Vector3, int> GenerateColorHistogram(Image image)
    {
        var pixels = image.WriteToMemory().ToArray();
        var histogram = new Dictionary<Vector3, int>();

        for (var i = 0; i < pixels.Length; i += 3)
        {
            var color = new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]);
            if (!histogram.TryAdd(color, 1))
            {
                histogram[color]++;
            }
        }

        return histogram;
    }

    private static bool IsColorCloseToWhiteOrBlack(Vector3 color)
    {
        var (_, _, lightness) = RgbToHsl(color);
        return lightness is > WhiteThreshold or < BlackThreshold;
    }

    private static List<Vector3> KMeansClustering(List<Vector3> points, int k, int maxIterations = 100)
    {
        var random = new Random();
        var centroids = points.OrderBy(x => random.Next()).Take(k).ToList();

        for (var i = 0; i < maxIterations; i++)
        {
            var clusters = new List<Vector3>[k];
            for (var j = 0; j < k; j++)
            {
                clusters[j] = [];
            }

            foreach (var point in points)
            {
                var nearestCentroidIndex = centroids
                    .Select((centroid, index) => new { Index = index, Distance = Vector3.DistanceSquared(centroid, point) })
                    .OrderBy(x => x.Distance)
                    .First().Index;
                clusters[nearestCentroidIndex].Add(point);
            }

            var newCentroids = clusters.Select(cluster =>
                cluster.Count != 0 ? new Vector3(
                    cluster.Average(p => p.X),
                    cluster.Average(p => p.Y),
                    cluster.Average(p => p.Z)
                ) : Vector3.Zero
            ).ToList();

            if (centroids.SequenceEqual(newCentroids))
                break;

            centroids = newCentroids;
        }

        return centroids;
    }

    public static List<Vector3> SortByBrightness(List<Vector3> colors)
    {
        return colors.OrderBy(c => 0.299 * c.X + 0.587 * c.Y + 0.114 * c.Z).ToList();
    }

    private static List<Vector3> SortByVibrancy(List<Vector3> colors)
    {
        return colors.OrderByDescending(c =>
        {
            var max = Math.Max(c.X, Math.Max(c.Y, c.Z));
            var min = Math.Min(c.X, Math.Min(c.Y, c.Z));
            return (max - min) / max;
        }).ToList();
    }

    private static bool IsCloseToWhiteOrBlack(Vector3 color)
    {
        var threshold = 30;
        return (color.X > 255 - threshold && color.Y > 255 - threshold && color.Z > 255 - threshold) ||
               (color.X < threshold && color.Y < threshold && color.Z < threshold);
    }

    private static string RgbToHex(Vector3 color)
    {
        return $"#{(int)color.X:X2}{(int)color.Y:X2}{(int)color.Z:X2}";
    }

    private static Vector3 GetComplementaryColor(Vector3 color)
    {
        // Convert RGB to HSL
        var (h, s, l) = RgbToHsl(color);

        // Rotate hue by 180 degrees
        h = (h + 180) % 360;

        // Convert back to RGB
        return HslToRgb(h, s, l);
    }

    private static (double H, double S, double L) RgbToHsl(Vector3 rgb)
    {
        double r = rgb.X / 255;
        double g = rgb.Y / 255;
        double b = rgb.Z / 255;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var diff = max - min;

        double h = 0;
        double s = 0;
        var l = (max + min) / 2;

        if (Math.Abs(diff) > 0.00001)
        {
            s = l > 0.5 ? diff / (2 - max - min) : diff / (max + min);

            if (max == r)
                h = (g - b) / diff + (g < b ? 6 : 0);
            else if (max == g)
                h = (b - r) / diff + 2;
            else if (max == b)
                h = (r - g) / diff + 4;

            h *= 60;
        }

        return (h, s, l);
    }

    private static Vector3 HslToRgb(double h, double s, double l)
    {
        double r, g, b;

        if (Math.Abs(s) < 0.00001)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 120);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 120);
        }

        return new Vector3((float)(r * 255), (float)(g * 255), (float)(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 360;
        if (t > 360) t -= 360;
        return t switch
        {
            < 60 => p + (q - p) * t / 60,
            < 180 => q,
            < 240 => p + (q - p) * (240 - t) / 60,
            _ => p
        };
    }

    /// <summary>
    /// Generates the Primary and Secondary colors from a file
    /// </summary>
    /// <remarks>This may use a second most common color or a complementary color. It's up to implemenation to choose what's best</remarks>
    /// <param name="sourceFile"></param>
    /// <returns></returns>
    public static ColorScape CalculateColorScape(string sourceFile)
    {
        if (!File.Exists(sourceFile)) return new ColorScape() {Primary = null, Secondary = null};

        var colors = GetPrimarySecondaryColors(sourceFile);

        return new ColorScape()
        {
            Primary = colors.Item1 == null ? null : RgbToHex(colors.Item1.Value),
            Secondary = colors.Item2 == null ? null : RgbToHex(colors.Item2.Value)
        };
    }



    /// <inheritdoc />
    public string CreateThumbnailFromBase64(string encodedImage, string fileName, EncodeFormat encodeFormat, int thumbnailWidth = ThumbnailWidth, string? targetDirectory = null)
    {
        // TODO: This code has no concept of cropping nor Thumbnail Size
        try
        {
            targetDirectory ??= directoryService.CoverImageDirectory;
            using var thumbnail = Image.ThumbnailBuffer(Convert.FromBase64String(encodedImage), thumbnailWidth);

            fileName += encodeFormat.GetExtension();
            thumbnail.WriteToFile(directoryService.FileSystem.Path.Join(targetDirectory, fileName));

            return fileName;
        }
        catch (FormatException e)
        {
            throw new KavitaException("Invalid Base64 string", e);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error creating thumbnail from url");
        }

        return string.Empty;
    }

    public string CreateThumbnailFromFile(string sourceFile, string fileName, EncodeFormat encodeFormat, int thumbnailWidth = ThumbnailWidth, string? targetDirectory = null)
    {
        try
        {
            targetDirectory ??= directoryService.CoverImageDirectory;
            using var thumbnail = Image.Thumbnail(sourceFile, thumbnailWidth);

            fileName += encodeFormat.GetExtension();
            thumbnail.WriteToFile(directoryService.FileSystem.Path.Join(targetDirectory, fileName));

            return fileName;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error creating thumbnail from file {SourceFile}", sourceFile);
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public string CreateTitleCover(string title, string? subtitle, string fileName, EncodeFormat encodeFormat,
        CoverImageSize size = CoverImageSize.Default, string? targetDirectory = null)
    {
        try
        {
            targetDirectory ??= directoryService.CoverImageDirectory;
            var (width, height) = size.GetDimensions();
            var svg = BuildTitleCoverSvg(title, subtitle, width, height);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            return WriteCoverThumbnail(stream, fileName, targetDirectory, encodeFormat, size);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[CreateTitleCover] There was an error generating a title cover for {Title}", title);
            return string.Empty;
        }
    }

    private static string BuildTitleCoverSvg(string title, string? subtitle, int width, int height)
    {
        var palette = GetTitleCoverPalette(title);
        var safeTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        var titleLines = WrapTitleCoverText(safeTitle, 14, 6);
        var subtitleText = string.IsNullOrWhiteSpace(subtitle) ? "TEXT" : subtitle.Trim().ToUpperInvariant();

        var titleFontSize = Math.Max(26, width / 9);
        var titleLineHeight = (int) Math.Round(titleFontSize * 1.22);
        var subtitleFontSize = Math.Max(16, width / 18);
        var titleBlockHeight = titleLines.Count * titleLineHeight;
        var startY = Math.Max(height / 4, (height - titleBlockHeight) / 2);
        var centerX = width / 2;

        var titleTspans = new StringBuilder();
        for (var index = 0; index < titleLines.Count; index++)
        {
            var dy = index == 0 ? 0 : titleLineHeight;
            titleTspans.Append(CultureInvariant(
                $"<tspan x=\"{centerX}\" dy=\"{dy}\">{EscapeSvg(titleLines[index])}</tspan>"));
        }

        return CultureInvariant($"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="{palette.BackgroundA}"/>
                  <stop offset="100%" stop-color="{palette.BackgroundB}"/>
                </linearGradient>
              </defs>
              <rect width="{width}" height="{height}" fill="url(#bg)"/>
              <rect x="{width * 0.08:0}" y="{height * 0.08:0}" width="{width * 0.84:0}" height="{height * 0.84:0}" rx="{width * 0.035:0}" fill="none" stroke="{palette.Line}" stroke-width="{Math.Max(2, width / 70)}" opacity="0.72"/>
              <rect x="{width * 0.14:0}" y="{height * 0.15:0}" width="{width * 0.72:0}" height="{Math.Max(3, height / 110)}" fill="{palette.Accent}" opacity="0.9"/>
              <text x="{centerX}" y="{startY}" text-anchor="middle" fill="{palette.Text}" font-family="NanumGothic, Nanum Gothic, Noto Sans CJK KR, Noto Sans KR, Apple SD Gothic Neo, Malgun Gothic, DejaVu Sans, sans-serif" font-size="{titleFontSize}" font-weight="700">{titleTspans}</text>
              <text x="{centerX}" y="{height * 0.82:0}" text-anchor="middle" fill="{palette.Text}" opacity="0.76" font-family="NanumGothic, Nanum Gothic, Noto Sans CJK KR, Noto Sans KR, Apple SD Gothic Neo, Malgun Gothic, DejaVu Sans, sans-serif" font-size="{subtitleFontSize}" font-weight="600" letter-spacing="{Math.Max(1, width / 160)}">{EscapeSvg(subtitleText)}</text>
            </svg>
            """);
    }

    private static (string BackgroundA, string BackgroundB, string Accent, string Line, string Text) GetTitleCoverPalette(string title)
    {
        var palettes = new[]
        {
            ("#1d3b53", "#0f1f2e", "#e6b450", "#d7dce2", "#f7f3ea"),
            ("#3a2d4f", "#181526", "#8bd3dd", "#f4d35e", "#fbfbff"),
            ("#243b2f", "#111f1a", "#f2a65a", "#dce3d5", "#f5f1e8"),
            ("#4a2f35", "#211318", "#7dd3fc", "#f2d0a4", "#fff7ed"),
            ("#263238", "#12191c", "#c3e88d", "#89ddff", "#f5f7fa"),
            ("#40342f", "#1d1714", "#ffd166", "#e9c46a", "#fff8e7"),
        };

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(title ?? string.Empty));
        return palettes[hash[0] % palettes.Length];
    }

    private static List<string> WrapTitleCoverText(string text, int maxUnitsPerLine, int maxLines)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var currentUnits = 0;
        var truncated = false;

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.ToString();
            var units = GetCoverTextUnits(rune);
            if (char.IsWhiteSpace(value[0]))
            {
                if (current.Length > 0 && currentUnits + 1 <= maxUnitsPerLine)
                {
                    current.Append(' ');
                    currentUnits++;
                }
                continue;
            }

            if (current.Length > 0 && currentUnits + units > maxUnitsPerLine)
            {
                lines.Add(current.ToString().Trim());
                current.Clear();
                currentUnits = 0;
                if (lines.Count == maxLines)
                {
                    truncated = true;
                    break;
                }
            }

            current.Append(value);
            currentUnits += units;
        }

        if (lines.Count < maxLines && current.Length > 0)
        {
            lines.Add(current.ToString().Trim());
        }

        if (lines.Count == 0)
        {
            lines.Add("Untitled");
        }

        if (truncated && !text.EndsWith(lines[^1], StringComparison.Ordinal))
        {
            lines[^1] = lines[^1].TrimEnd('.') + "...";
        }

        return lines;
    }

    private static int GetCoverTextUnits(Rune rune)
    {
        return rune.Value switch
        {
            >= 0x1100 and <= 0x11FF => 2,
            >= 0x3130 and <= 0x318F => 2,
            >= 0xAC00 and <= 0xD7AF => 2,
            >= 0x3040 and <= 0x30FF => 2,
            >= 0x3400 and <= 0x9FFF => 2,
            _ => 1
        };
    }

    private static string EscapeSvg(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static string CultureInvariant(FormattableString value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<string> CreateThumbnailFromUrl(string url, string fileName, EncodeFormat encodeFormat, int thumbnailWidth = ThumbnailWidth)
    {
        try
        {
            var imageStream = await FlurlConfiguration.CreateSafeRequest(url)
                .AllowHttpStatus("2xx,304")
                .GetStreamAsync();

            using var thumbnail = Image.ThumbnailStream(imageStream, thumbnailWidth);

            fileName += encodeFormat.GetExtension();
            thumbnail.WriteToFile(directoryService.FileSystem.Path.Join(directoryService.CoverImageDirectory, fileName));

            return fileName;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error creating thumbnail from url");
        }

        return string.Empty;
    }


    /// <summary>
    /// Returns the name format for a chapter cover image
    /// </summary>
    /// <param name="chapterId"></param>
    /// <param name="volumeId"></param>
    /// <returns></returns>
    public static string GetChapterFormat(int chapterId, int volumeId)
    {
        return $"v{volumeId}_c{chapterId}";
    }

    /// <summary>
    /// Returns the name format for a volume cover image (custom)
    /// </summary>
    /// <param name="volumeId"></param>
    /// <returns></returns>
    public static string GetVolumeFormat(int volumeId)
    {
        return $"v{volumeId}";
    }

    /// <summary>
    /// Returns the name format for a library cover image
    /// </summary>
    /// <param name="libraryId"></param>
    /// <returns></returns>
    public static string GetLibraryFormat(int libraryId)
    {
        return $"l{libraryId}";
    }

    /// <summary>
    /// Returns the name format for a series cover image
    /// </summary>
    /// <param name="seriesId"></param>
    /// <returns></returns>
    public static string GetSeriesFormat(int seriesId)
    {
        return $"series{seriesId}"; // If this ever changes, also needs to update in SeriesRepository#GetAllWithCoversInDifferentEncodingAsync
    }

    /// <summary>
    /// Returns the name format for a collection tag cover image
    /// </summary>
    /// <param name="tagId"></param>
    /// <returns></returns>
    public static string GetCollectionTagFormat(int tagId)
    {
        return $"tag{tagId}";
    }

    /// <summary>
    /// Returns the name format for a reading list cover image
    /// </summary>
    /// <param name="readingListId"></param>
    /// <returns></returns>
    public static string GetReadingListFormat(int readingListId)
    {
        // ReSharper disable once StringLiteralTypo
        return $"readinglist{readingListId}";
    }

    /// <summary>
    /// Returns the name format for a thumbnail (temp thumbnail)
    /// </summary>
    /// <param name="chapterId"></param>
    /// <returns></returns>
    public static string GetThumbnailFormat(int chapterId)
    {
        return $"thumbnail{chapterId}";
    }

    /// <summary>
    /// Returns the name format for a person cover
    /// </summary>
    /// <param name="personId"></param>
    /// <returns></returns>
    public static string GetPersonFormat(int personId)
    {
        return $"person{personId}";
    }

    /// <summary>
    /// Returns the name format for a user cover
    /// </summary>
    /// <param name="userId"></param>
    /// <returns></returns>
    public static string GetUserFormat(int userId)
    {
        return $"user{userId}";
    }

    public static string GetWebLinkFormat(string url, EncodeFormat encodeFormat)
    {
        return $"{new Uri(url).Host.Replace("www.", string.Empty)}{encodeFormat.GetExtension()}";
    }

    public static string GetPublisherFormat(string publisher, EncodeFormat encodeFormat)
    {
        return $"{publisher}{encodeFormat.GetExtension()}";
    }


    public static void CreateMergedImage(IList<string> coverImages, CoverImageSize size, string dest)
    {
        var (width, height) = size.GetDimensions();
        int rows, cols;

        if (coverImages.Count == 1)
        {
            rows = 1;
            cols = 1;
        }
        else if (coverImages.Count == 2)
        {
            rows = 1;
            cols = 2;
        }
        else
        {
            rows = 2;
            cols = 2;
        }


        var image = Image.Black(width, height);

        var thumbnailWidth = image.Width / cols;
        var thumbnailHeight = image.Height / rows;

        for (var i = 0; i < coverImages.Count; i++)
        {
            if (!File.Exists(coverImages[i])) continue;
            var tile = Image.NewFromFile(coverImages[i], access: Enums.Access.Sequential);
            tile = tile.ThumbnailImage(thumbnailWidth, height: thumbnailHeight);

            var row = i / cols;
            var col = i % cols;

            var x = col * thumbnailWidth;
            var y = row * thumbnailHeight;

            if (coverImages.Count == 3 && i == 2)
            {
                x = (image.Width - thumbnailWidth) / 2;
                y = thumbnailHeight;
            }

            image = image.Insert(tile, x, y);
        }

        image.WriteToFile(dest);
    }

    public void UpdateColorScape(IHasCoverImage entity)
    {
        var colors = CalculateColorScape(
            directoryService.FileSystem.Path.Join(directoryService.CoverImageDirectory, entity.CoverImage));
        entity.PrimaryColor = colors.Primary;
        entity.SecondaryColor = colors.Secondary;
    }


    public static (int R, int G, int B) HexToRgb(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) throw new ArgumentException("Hex cannot be null");

        // Remove the leading '#' if present
        hex = hex.TrimStart('#');

        // Ensure the hex string is valid
        if (hex.Length != 6 && hex.Length != 3)
        {
            throw new ArgumentException("Hex string should be 6 or 3 characters long.");
        }

        if (hex.Length == 3)
        {
            // Expand shorthand notation to full form (e.g., "abc" -> "aabbcc")
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        // Parse the hex string into RGB components
        var r = Convert.ToInt32(hex.Substring(0, 2), 16);
        var g = Convert.ToInt32(hex.Substring(2, 2), 16);
        var b = Convert.ToInt32(hex.Substring(4, 2), 16);

        return (r, g, b);
    }


}
