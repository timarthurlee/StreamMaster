﻿using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using SixLabors.ImageSharp;

using StreamMaster.Domain.Dto;
using StreamMaster.Domain.Enums;
using StreamMaster.Domain.Helpers;
namespace StreamMaster.SchedulesDirect;

public class LineupService : ILineupService
{
    private readonly ILogger<LineupService> logger;
    private readonly IOptionsMonitor<SDSettings> intSDSettings;
    private readonly IOptionsMonitor<Setting> settings;
    private readonly ILogoService logoService;
    private readonly ISchedulesDirectAPIService schedulesDirectAPI;
    private readonly IEPGCache<LineupResult> epgCache;
    private readonly ISchedulesDirectDataService schedulesDirectDataService;
    private readonly HttpClient httpClient;
    private readonly ConcurrentDictionary<string, StationImage> StationLogosToDownload = [];
    private readonly IImageDownloadQueue imageDownloadQueue; // Injected ImageDownloadQueue

    public LineupService(
        ILogger<LineupService> logger,
        IOptionsMonitor<SDSettings> intSDSettings,
         IOptionsMonitor<Setting> Settings,
        ILogoService logoService,
        ISchedulesDirectAPIService schedulesDirectAPI,
        IEPGCache<LineupResult> epgCache,
        ISchedulesDirectDataService schedulesDirectDataService,
        IImageDownloadQueue imageDownloadQueue
        )
    {
        settings = Settings;
        this.logger = logger;
        this.intSDSettings = intSDSettings;
        this.logoService = logoService;
        this.schedulesDirectAPI = schedulesDirectAPI;
        this.epgCache = epgCache;
        this.schedulesDirectDataService = schedulesDirectDataService;
        this.imageDownloadQueue = imageDownloadQueue;
        httpClient = CreateHttpClient();
    }

    private HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.CurrentValue.ClientUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    public void ClearCache()
    {
        StationLogosToDownload.Clear();
    }
    public void ResetCache()
    {
        epgCache.ResetCache();
    }

    //private void CheckHeadendView(SubscribedLineup subscribedLineup)
    //{
    //    if (!intSDSettings.CurrentValue.HeadendsToView.Any(a => a.Id == subscribedLineup.Id))
    //    {
    //        intSDSettings.CurrentValue.HeadendsToView.Add(new HeadendToView
    //        {
    //            Id = subscribedLineup.Id,
    //        });
    //        SettingsHelper.UpdateSetting(intSDSettings.CurrentValue);
    //    }
    //    //string HeadendId, string Country, string Postal
    //}

    public async Task<bool> BuildLineupServices(CancellationToken cancellationToken = default)
    {
        SDSettings sdSettings = intSDSettings.CurrentValue;
        LineupResponse? clientLineups = await GetSubscribedLineups(cancellationToken).ConfigureAwait(false);

        if (clientLineups == null || clientLineups.Lineups.Count < 1)
        {
            return true;
        }

        string preferredLogoStyle = string.IsNullOrEmpty(sdSettings.PreferredLogoStyle) ? "DARK" : sdSettings.PreferredLogoStyle;
        string alternateLogoStyle = string.IsNullOrEmpty(sdSettings.AlternateLogoStyle) ? "WHITE" : sdSettings.AlternateLogoStyle;
        ISchedulesDirectData schedulesDirectData = schedulesDirectDataService.SchedulesDirectData();

        foreach (SubscribedLineup clientLineup in clientLineups.Lineups)
        {
            if (clientLineup.IsDeleted)
            {
                if (sdSettings.SDStationIds.Any(a => a.Lineup == clientLineup.Lineup))
                {
                    sdSettings.SDStationIds.RemoveAll(a => a.Lineup == clientLineup.Lineup);
                    SettingsHelper.UpdateSetting(sdSettings);
                }
            }

            //CheckHeadendView(clientLineup);

            if (sdSettings.SDStationIds.Find(a => a.Lineup == clientLineup.Lineup) == null)
            {
                return true;
            }

            MxfLineup mxfLineup = schedulesDirectData.FindOrCreateLineup(clientLineup.Lineup, $"SM {clientLineup.Name} ({clientLineup.Location})");
            StationChannelMap? lineupMap = await GetStationChannelMap(clientLineup.Lineup);

            if (lineupMap == null || lineupMap.Stations == null || lineupMap.Stations.Count == 0)
            {
                logger.LogError("Subscribed lineup {clientLineup.Lineup} does not contain any stations.", clientLineup.Lineup);
                continue;
            }

            ConcurrentHashSet<string> channelNumbers = [];

            foreach (LineupStation? station in lineupMap.Stations)
            {
                if (station == null || sdSettings.SDStationIds.Find(a => a.StationId == station.StationId) == null)
                {
                    continue;
                }

                string serviceName = station.StationId;// $"{EPGHelper.SchedulesDirectId}-{station.StationId}";
                MxfService mxfService = schedulesDirectData.FindOrCreateService(serviceName);

                if (string.IsNullOrEmpty(mxfService.CallSign))
                {
                    SetStationDetails(station, mxfService, preferredLogoStyle, alternateLogoStyle);
                }

                foreach (LineupChannelStation map in lineupMap.Map.Where(a => a.StationId == station.StationId))
                {
                    const string channelNumber = "1"; // Adjust as necessary
                    if (channelNumbers.Add($"{channelNumber}:{station.StationId}"))
                    {
                        mxfLineup.Channels.Add(new MxfChannel(mxfLineup, mxfService)
                        {
                            MatchName = ""
                        });
                    }
                }
            }
        }

        // Trigger ImageDownloadService by adding the images to the queue
        if (!StationLogosToDownload.IsEmpty)
        {
            foreach (KeyValuePair<string, StationImage> serviceLogo in StationLogosToDownload)
            {
                //string ext = Path.GetExtension(serviceLogo.Value.Url);
                //if (string.IsNullOrEmpty(ext))
                //{
                //    ext = ".png";
                //}

                NameLogo nameLogo = new(serviceLogo.Key, serviceLogo.Value.Url, SMFileTypes.SDStationLogo, false);

                //logoService.AddLogo();

                imageDownloadQueue.EnqueueNameLogo(nameLogo);
            }

            logger.LogInformation("Enqueued {StationLogosToDownload.Count} station logos for download.", StationLogosToDownload.Count);
        }

        StationLogosToDownload.Clear();

        if (!schedulesDirectData.Services.IsEmpty)
        {
            UpdateIcons(schedulesDirectData.Services.Values);
            logger.LogInformation("Exiting BuildLineupServices(). SUCCESS.");
            epgCache.SaveCache();
            return true;
        }

        logger.LogWarning("There are 0 stations queued for download.");
        return false;
    }

    public async IAsyncEnumerable<LogoFileDto> GetLogos([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SDSettings sdSettings = intSDSettings.CurrentValue;

        string preferredLogoStyle = string.IsNullOrEmpty(sdSettings.PreferredLogoStyle) ? "DARK" : sdSettings.PreferredLogoStyle;
        string alternateLogoStyle = string.IsNullOrEmpty(sdSettings.AlternateLogoStyle) ? "WHITE" : sdSettings.AlternateLogoStyle;

        LineupResponse? clientLineups = await GetSubscribedLineups(cancellationToken).ConfigureAwait(false);

        if (clientLineups == null || clientLineups.Lineups.Count < 1)
        {
            yield break;
        }

        // Convert lists to HashSets for efficient lookup
        HashSet<string> lineupSet = [.. sdSettings.SDStationIds.Select(a => a.Lineup)];
        HashSet<string> stationIdSet = [.. sdSettings.SDStationIds.Select(a => a.StationId)];

        foreach (SubscribedLineup clientLineup in clientLineups.Lineups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (clientLineup.IsDeleted)
            {
                continue;
            }

            if (!lineupSet.Contains(clientLineup.Lineup))
            {
                continue;
            }

            StationChannelMap? lineupMap = await GetStationChannelMap(clientLineup.Lineup);

            if (lineupMap == null || lineupMap.Stations == null || lineupMap.Stations.Count == 0)
            {
                logger.LogError("Subscribed lineup '{Lineup}' does not contain any stations.", clientLineup.Lineup);
                continue;
            }

            foreach (LineupStation? station in lineupMap.Stations)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                if (station == null || !stationIdSet.Contains(station.StationId))
                {
                    continue;
                }

                StationImage? stationLogo = GetStationLogo(station, preferredLogoStyle, alternateLogoStyle);
                if (stationLogo is null)
                {
                    logger.LogWarning("No logo found for station {StationId}", station.StationId);
                    continue;
                }

                string title = string.IsNullOrEmpty(station.Callsign) ? station.Name : $"{station.Callsign} {station.Name}";

                NameLogo nl = new(title, stationLogo.Url, SMFileTypes.SDStationLogo, false);
                LogoFileDto d = new()
                {
                    Source = nl.FileName,
                    Value = stationLogo.Url,
                    SMFileType = SMFileTypes.SDStationLogo,
                    Name = title
                };

                yield return d;
            }
        }
    }


    private void SetStationDetails(LineupStation station, MxfService mxfService, string preferredLogoStyle, string alternateLogoStyle)
    {
        if (!string.IsNullOrEmpty(mxfService.CallSign))
        {
            return; // If the callsign is already set, there's no need to continue
        }

        // Set the UID and CallSign for the service
        //mxfService.UidOverride = $"!Service!STREAMMASTER_{station.StationId}";
        mxfService.CallSign = station.Callsign;

        // Set the service name based on the station name and callsign
        if (string.IsNullOrEmpty(mxfService.Name))
        {
            mxfService.Name = Regex.Matches(station.Name.Replace("-", ""), station.Callsign).Count > 0
                ? !string.IsNullOrEmpty(station.Affiliate) ? $"{station.Name} ({station.Affiliate})" : station.Name
                : station.Name;
        }

        // Add the affiliate if it exists
        if (!string.IsNullOrEmpty(station.Affiliate))
        {
            mxfService.mxfAffiliate = schedulesDirectDataService.SchedulesDirectData().FindOrCreateAffiliate(station.Affiliate);
        }

        // Handle station logo if available
        StationImage? stationLogo = GetStationLogo(station, preferredLogoStyle, alternateLogoStyle);
        if (stationLogo != null)
        {
            // string logoFilename = $"{stationLogo.Md5}.png";
            //string logoPath = Path.Combine(BuildInfo.SDStationLogosCacheFolder, logoFilename);

            //if (!await FileExistsAsync(logoPath).ConfigureAwait(false))
            //{

            //new KeyValuePair<MxfService, string[]>(mxfService, [logoPath, stationLogo.Url])
            StationLogosToDownload.TryAdd(stationLogo.Md5, stationLogo);
            string title = string.IsNullOrEmpty(station.Callsign) ? station.Name : $"{station.Callsign} {station.Name}";

            NameLogo nl = new(title, stationLogo.Url, SMFileTypes.SDStationLogo, false);
            LogoFileDto d = new() { Source = nl.FileName, Value = stationLogo.Url, SMFileType = SMFileTypes.SDStationLogo, Name = title };
            logoService.AddLogo(d);
            //}
            //else
            //{
            //    await SetLogoAsync(mxfService, logoPath, stationLogo, cancellationToken).ConfigureAwait(false);
            //}
            //await SetLogoAsync(mxfService, stationLogo, cancellationToken).ConfigureAwait(false);
            mxfService.mxfGuideImage = schedulesDirectDataService.SchedulesDirectData().FindOrCreateGuideImage(stationLogo.Url);
            //await HandleStationLogoAsync(mxfService, stationLogo, cancellationToken);
        }
    }


    private static StationImage? GetStationLogo(LineupStation station, string preferredLogoStyle, string alternateLogoStyle)
    {
        // Select the logo based on preferred or alternate styles, falling back to the default logo
        return station.StationLogos?.FirstOrDefault(arg => arg.Category?.EqualsIgnoreCase(preferredLogoStyle) == true)
               ?? station.StationLogos?.FirstOrDefault(arg => arg.Category?.EqualsIgnoreCase(alternateLogoStyle) == true)
               ?? station.Logo;
    }

    //private async Task HandleStationLogoAsync(MxfService mxfService, StationImage stationLogo, CancellationToken cancellationToken)
    //{
    //    string logoFilename = $"{stationLogo.Md5}.png";
    //    //string logoPath = Path.Combine(BuildInfo.SDStationLogosCacheFolder, logoFilename);

    //    //if (!await FileExistsAsync(logoPath).ConfigureAwait(false))
    //    //{

    //    //new KeyValuePair<MxfService, string[]>(mxfService, [logoPath, stationLogo.Url])
    //    StationLogosToDownload.TryAdd(mxfService.CallSign, stationLogo.Url);
    //    //}
    //    //else
    //    //{
    //    //    await SetLogoAsync(mxfService, logoPath, stationLogo, cancellationToken).ConfigureAwait(false);
    //    //}
    //    await SetLogoAsync(mxfService, stationLogo, cancellationToken).ConfigureAwait(false);
    //}

    //private async Task SetLogoAsync(MxfService mxfService, StationImage stationLogo, CancellationToken cancellationToken)
    //{
    //    //if (File.Exists(logoPath))
    //    //{
    //    //    await using FileStream stream = File.OpenRead(logoPath);
    //    //    using Image image = await Image.LoadAsync(stream, cancellationToken);

    //    //    mxfService.extras.TryAdd("logo", new StationImage
    //    //    {
    //    //        Url = stationLogo.Url,
    //    //        Height = image.Height,
    //    //        Width = image.Width
    //    //    });
    //    //}
    //    //else
    //    //{
    //    //    mxfService.extras.TryAdd("logo", new StationImage
    //    //    {
    //    //        Url = stationLogo.Url
    //    //    });
    //    //}

    //    mxfService.mxfGuideImage = schedulesDirectDataService.SchedulesDirectData().FindOrCreateGuideImage(stationLogo.Url);
    //}

    //private static async Task<bool> FileExistsAsync(string filePath)
    //{
    //    return await Task.Run(() => File.Exists(filePath)).ConfigureAwait(false);
    //}

    //MaxParallelDownloads

    //private async Task<bool> DownloadStationLogos(CancellationToken cancellationToken)
    //{
    //    using SemaphoreSlim throttler = new(SchedulesDirect.MaxParallelDownloads);

    //    IEnumerable<Task> tasks = StationLogosToDownload.Select(async serviceLogo =>
    //    {
    //        await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
    //        try
    //        {
    //            await DownloadSdLogoAsync(serviceLogo.Value[1], serviceLogo.Value[0], cancellationToken).ConfigureAwait(false);
    //        }
    //        finally
    //        {
    //            throttler.Release();
    //        }
    //    });

    //    await Task.WhenAll(tasks).ConfigureAwait(false);
    //    StationLogosToDownload.Clear();
    //    return true;
    //}

    //private async Task<(int width, int height)> DownloadSdLogoAsync(string uri, string filePath, CancellationToken cancellationToken)
    //{
    //    try
    //    {
    //        HttpResponseMessage response = await httpClient.GetAsync(uri, cancellationToken);

    //        if (response.IsSuccessStatusCode)
    //        {
    //            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    //            using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);

    //            if (image == null)
    //            {
    //                throw new InvalidOperationException("Failed to load image");
    //            }

    //            await using FileStream outputFileStream = File.Create(filePath);
    //            image.Save(outputFileStream, image.Metadata.DecodedImageFormat);

    //            return (image.Width, image.Height);
    //        }
    //        else
    //        {
    //            logger.LogError("HTTP request failed with status Code: {StatusCode}", response.StatusCode);
    //        }
    //    }
    //    catch (IOException ioEx)
    //    {
    //        logger.LogError(ioEx, "File system error occurred while downloading the logo");
    //    }
    //    catch (Exception ex)
    //    {
    //        logger.LogError(ex, "An exception occurred during DownloadSdLogoAsync.");
    //    }

    //    return (0, 0);
    //}

    private void UpdateIcons(ICollection<MxfService> Services)
    {
        foreach (MxfService? service in Services.Where(a => a.extras.ContainsKey("logo")))
        {
            StationImage artwork = service.extras["logo"];
            //logoService.AddLogo(artwork.Url, service.CallSign);
        }
    }

    private async Task<LineupResponse?> GetSubscribedLineups(CancellationToken cancellationToken)
    {
        LineupResponse? response = await schedulesDirectAPI.GetApiResponse<LineupResponse>(APIMethod.GET, "lineups", cancellationToken, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<StationChannelMap?> GetStationChannelMap(string lineup)
    {
        return await schedulesDirectAPI.GetApiResponse<StationChannelMap>(APIMethod.GET, $"lineups/{lineup}").ConfigureAwait(false);
    }

    public async Task<List<SubscribedLineup>> GetLineups(CancellationToken cancellationToken = default)
    {
        if (!intSDSettings.CurrentValue.SDEnabled)
        {
            return [];
        }

        LineupResponse? lineups = await GetSubscribedLineups(cancellationToken).ConfigureAwait(false);
        return lineups?.Lineups ?? [];
    }

    public async Task<List<StationChannelMap>> GetStationChannelMaps(CancellationToken cancellationToken)
    {
        List<StationChannelMap> result = [];
        List<Station> stations = await GetStations(cancellationToken).ConfigureAwait(false);

        foreach (Station station in stations)
        {
            StationChannelMap? stationChannelMap = await GetStationChannelMap(station.Lineup).ConfigureAwait(false);
            if (stationChannelMap != null)
            {
                result.Add(stationChannelMap);
            }
        }

        return result;
    }

    public async Task<List<StationPreview>> GetStationPreviews(CancellationToken cancellationToken)
    {
        List<Station> stations = await GetStations(cancellationToken).ConfigureAwait(false);
        if (stations == null || stations.Count == 0)
        {
            return [];
        }

        List<StationPreview> previews = [];
        foreach (Station station in stations)
        {
            StationPreview preview = new(station)
            {
                Affiliate = station.Affiliate ?? string.Empty
            };
            previews.Add(preview);
        }

        return previews;
    }

    private async Task<List<Station>> GetStations(CancellationToken cancellationToken)
    {
        List<Station> stations = [];
        List<SubscribedLineup> lineups = await GetLineups(cancellationToken).ConfigureAwait(false);

        foreach (SubscribedLineup lineup in lineups)
        {
            LineupResult? lineupResult = await GetLineup(lineup.Lineup, cancellationToken).ConfigureAwait(false);
            if (lineupResult == null)
            {
                continue;
            }

            stations.AddRange(lineupResult.Stations.Select(station =>
            {
                station.Lineup = lineup.Lineup;
                return station;
            }));
        }

        return stations;
    }

    private async Task<LineupResult?> GetLineup(string lineup, CancellationToken cancellationToken)
    {
        LineupResult? cachedLineup = await epgCache.GetValidCachedDataAsync("Lineup-" + lineup, cancellationToken).ConfigureAwait(false);
        if (cachedLineup != null)
        {
            return cachedLineup;
        }

        LineupResult? lineupResult = await schedulesDirectAPI.GetApiResponse<LineupResult>(APIMethod.GET, $"lineups/{lineup}", cancellationToken, cancellationToken).ConfigureAwait(false);
        if (lineupResult != null)
        {
            await epgCache.WriteToCacheAsync("Lineup-" + lineup, lineupResult, cancellationToken).ConfigureAwait(false);
        }

        return lineupResult;
    }

    public void Dispose()
    {
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}