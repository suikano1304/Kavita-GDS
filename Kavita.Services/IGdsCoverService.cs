using System.Collections.Generic;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;

namespace Kavita.Services;

public interface IGdsCoverService
{
    Task<GdsCoverGenerationResult> ProcessSeriesCoverGen(Series series, bool forceUpdate, EncodeFormat encodeFormat,
        CoverImageSize coverImageSize, bool forceColorScape = false);
}

public sealed record GdsCoverGenerationResult(bool Handled, IReadOnlyCollection<SignalRMessage> UpdateEvents);
