using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentResults;
using Microsoft.Extensions.Options;
using TNRD.Zeepkist.WorkshopApi.Drone.Api;
using TNRD.Zeepkist.WorkshopApi.Drone.Data;
using TNRD.Zeepkist.WorkshopApi.Drone.FluentResults;
using TNRD.Zeepkist.WorkshopApi.Drone.Google;
using TNRD.Zeepkist.WorkshopApi.Drone.ResponseModels;
using TNRD.Zeepkist.WorkshopApi.Drone.Steam;

namespace TNRD.Zeepkist.WorkshopApi.Drone;

public class Worker : BackgroundService
{
    private const int MAX_EMPTY_PAGES = 5;

    private readonly ILogger<Worker> logger;
    private readonly ILogger<DepotDownloader.DepotDownloader> depotDownloaderLogger;
    private readonly SteamClient steamClient;
    private readonly ApiClient apiClient;
    private readonly IUploadService uploadService;
    private readonly SteamOptions steamOptions;

    public Worker(
        ILogger<Worker> logger,
        SteamClient steamClient,
        ApiClient apiClient,
        IUploadService uploadService,
        // ReSharper disable once ContextualLoggerProblem
        ILogger<DepotDownloader.DepotDownloader> depotDownloaderLogger,
        IOptions<SteamOptions> steamOptions
    )
    {
        this.logger = logger;
        this.steamClient = steamClient;
        this.apiClient = apiClient;
        this.uploadService = uploadService;
        this.depotDownloaderLogger = depotDownloaderLogger;
        this.steamOptions = steamOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int timeToWait = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            DepotDownloader.DepotDownloader.Initialize(depotDownloaderLogger);

            try
            {
                await ExecuteByModified(stoppingToken);
                await ExecuteByCreated(stoppingToken);

                timeToWait = 5;
                logger.LogInformation("Waiting 1 minute before checking again");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "Unhandled exception");
                logger.LogInformation("Waiting 5 minutes before trying again");
                await Task.Delay(TimeSpan.FromMinutes(timeToWait), stoppingToken);
                timeToWait *= 2;
            }

            DepotDownloader.DepotDownloader.Dispose();

            GC.Collect();
        }
    }

    private async Task ExecuteByCreated(CancellationToken stoppingToken)
    {
        int page = 1;
        int totalPages = await steamClient.GetTotalPages(false, stoppingToken);

        Result<LevelResponseModel> lastCreatedResult = await apiClient.GetLastCreated();
        if (lastCreatedResult.IsFailed)
        {
            logger.LogError("Unable to get last created: {Result}", lastCreatedResult.ToString());
            return;
        }

        int amountEmpty = 0;

        while (!stoppingToken.IsCancellationRequested && amountEmpty < MAX_EMPTY_PAGES)
        {
            logger.LogInformation("Getting page {Page}/{Total}", page, totalPages);
            Response response = await steamClient.GetResponse(page, false, stoppingToken);

            if (await ProcessResponse(response, stoppingToken))
                amountEmpty++;
            else
                amountEmpty = 0;

            page++;
        }
    }

    private async Task ExecuteByModified(CancellationToken stoppingToken)
    {
        int page = 1;
        int totalPages = await steamClient.GetTotalPages(true, stoppingToken);

        Result<LevelResponseModel> lastUpdatedResult = await apiClient.GetLastUpdated();
        if (lastUpdatedResult.IsFailed)
        {
            logger.LogError("Unable to get last updated: {Result}", lastUpdatedResult.ToString());
            return;
        }

        int amountEmpty = 0;

        while (!stoppingToken.IsCancellationRequested && amountEmpty < MAX_EMPTY_PAGES)
        {
            logger.LogInformation("Getting page {Page}/{Total}", page, totalPages);
            Response response = await steamClient.GetResponse(page, true, stoppingToken);

            if (await ProcessResponse(response, stoppingToken))
                amountEmpty++;
            else
                amountEmpty = 0;

            page++;
        }
    }

    private async Task<bool> ProcessResponse(Response response, CancellationToken stoppingToken)
    {
        logger.LogInformation("Filtering items");
        List<PublishedFileDetails> filtered = await Filter(response);
        int totalFalsePositives = 0;

        logger.LogInformation("Processing {Count} items", filtered.Count);
        foreach (PublishedFileDetails publishedFileDetails in filtered)
        {
            logger.LogInformation("Downloading {WorkshopId}", publishedFileDetails.PublishedFileId);
            await DepotDownloader.DepotDownloader.Run(publishedFileDetails.PublishedFileId,
                steamOptions.MountDestination);

            List<string> files = Directory
                .EnumerateFiles(steamOptions.MountDestination, "*.zeeplevel", SearchOption.AllDirectories).ToList();

            await DeleteMissingLevels(publishedFileDetails, files);

            int falsePositives = 0;
            foreach (string path in files)
            {
                logger.LogInformation("Processing '{Path}'", path);
                Result<bool> processResult = await ProcessItem(path,
                    publishedFileDetails,
                    publishedFileDetails.PublishedFileId,
                    stoppingToken);

                if (processResult.IsFailed)
                {
                    logger.LogError("Unable to process item: {Result}", processResult);
                }
                else if (!processResult.Value)
                {
                    falsePositives++;
                }
            }

            if (falsePositives == files.Count)
            {
                totalFalsePositives++;
                await EnsureFalsePositivesTimeUpdated(publishedFileDetails, files);
            }

            Directory.Delete(steamOptions.MountDestination, true);
        }

        return filtered.Count - totalFalsePositives == 0;
    }

    private async Task DeleteMissingLevels(PublishedFileDetails publishedFileDetails, List<string> files)
    {
        Result<IEnumerable<LevelResponseModel>> result =
            await apiClient.GetLevelsByWorkshopId(publishedFileDetails.PublishedFileId);

        if (result.IsFailed)
        {
            logger.LogError("Unable to get levels by workshop id: {Result}", result.ToString());
            return;
        }

        List<(string uid, string hash)> fileData = new();

        foreach (string file in files)
        {
            string uid = await GetUidFromFile(file, CancellationToken.None);
            string textToHash = await GetTextToHash(file, CancellationToken.None);
            string hash = Hash(textToHash);
            fileData.Add((uid, hash));
        }

        foreach (LevelResponseModel levelResponseModel in result.Value)
        {
            bool foundLevelInFile = false;

            foreach ((string uid, string hash) in fileData)
            {
                if (levelResponseModel.FileUid == uid && levelResponseModel.FileHash == hash)
                {
                    foundLevelInFile = true;
                    break;
                }
            }

            if (foundLevelInFile)
                continue;

            Result<LevelResponseModel> deleteResult = await apiClient.DeleteLevel(levelResponseModel.Id);

            if (deleteResult.IsFailed)
            {
                logger.LogError("Unable to delete level: {Result}", deleteResult.ToString());
            }
        }
    }

    private async Task EnsureFalsePositivesTimeUpdated(PublishedFileDetails publishedFileDetails, List<string> files)
    {
        Result<IEnumerable<LevelResponseModel>> result =
            await apiClient.GetLevelsByWorkshopId(publishedFileDetails.PublishedFileId);

        if (!result.IsSuccess)
        {
            logger.LogError("Unable to get levels by workshop id: {Result}", result.ToString());
            return;
        }

        bool sameAmount = result.Value.Count() == files.Count;

        if (sameAmount)
            return;

        foreach (LevelResponseModel levelResponseModel in result.Value)
        {
            if (levelResponseModel.UpdatedAt == publishedFileDetails.TimeUpdated)
                continue;

            Result<LevelResponseModel> updateResult = await apiClient.UpdateLevelTime(
                levelResponseModel.Id,
                new DateTimeOffset(publishedFileDetails.TimeUpdated).ToUnixTimeSeconds());

            if (updateResult.IsFailed)
            {
                logger.LogError("Unable to update level time: {Result}", updateResult.ToString());
            }
        }
    }

    private async Task<List<PublishedFileDetails>> Filter(Response response)
    {
        List<PublishedFileDetails> filtered = new();

        foreach (PublishedFileDetails details in response.PublishedFileDetails)
        {
            Result<IEnumerable<LevelResponseModel>> result =
                await apiClient.GetLevelsByWorkshopId(details.PublishedFileId);

            if (result.IsFailed)
            {
                filtered.Add(details);
                continue;
            }

            bool addToFiltered = false;

            foreach (LevelResponseModel model in result.Value)
            {
                if (model.ReplacedBy.HasValue)
                    continue;

                if (model.UpdatedAt < details.TimeUpdated)
                {
                    addToFiltered = true;
                }

                if (model.CreatedAt < details.TimeCreated)
                {
                    addToFiltered = true;
                }
            }

            if (addToFiltered)
            {
                filtered.Add(details);
            }
        }

        return filtered;
    }

    private async Task<Result<bool>> ProcessItem(
        string path,
        PublishedFileDetails item,
        string workshopId,
        CancellationToken stoppingToken
    )
    {
        string filename = Path.GetFileNameWithoutExtension(path);

        if (string.IsNullOrEmpty(filename) || string.IsNullOrWhiteSpace(filename))
        {
            logger.LogWarning("Filename for {WorkshopId} is empty", workshopId);
            filename = "[Unknown]";
        }

        Result<IEnumerable<LevelResponseModel>> getLevelsResult = await apiClient.GetLevelsByWorkshopId(workshopId);
        if (getLevelsResult.IsFailedWithNotFound())
        {
            return await HandleNewLevel(path, item, filename, stoppingToken);
        }

        if (getLevelsResult.IsSuccess)
        {
            return await HandleExistingItem(path, item, getLevelsResult, filename, stoppingToken);
        }

        logger.LogCritical("Unable to get levels from API; Result: {Result}", getLevelsResult.ToString());
        throw new Exception();
    }

    private async Task<Result<bool>> HandleNewLevel(
        string path,
        PublishedFileDetails item,
        string filename,
        CancellationToken stoppingToken
    )
    {
        try
        {
            Result<int> createResult = await CreateNewLevel(path, filename, item, stoppingToken);
            return createResult.IsSuccess ? Result.Ok(true) : createResult.ToResult();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unable to create new level");
            return Result.Fail(new ExceptionalError(e));
        }
    }

    private async Task<Result<bool>> HandleExistingItem(
        string path,
        PublishedFileDetails item,
        Result<IEnumerable<LevelResponseModel>> getLevelsResult,
        string filename,
        CancellationToken stoppingToken
    )
    {
        LevelResponseModel? existingItem = getLevelsResult.Value.FirstOrDefault(x =>
            x.Name == filename && x.AuthorId == item.Creator && x.ReplacedBy == null);

        if (existingItem == null)
        {
            try
            {
                Result<int> createResult = await CreateNewLevel(path, filename, item, stoppingToken);
                return createResult.IsSuccess ? Result.Ok(true) : createResult.ToResult();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to create new level");
                return Result.Fail(new ExceptionalError(e));
            }
        }

        if (item.TimeCreated > existingItem.CreatedAt || item.TimeUpdated > existingItem.UpdatedAt)
        {
            return await ReplaceExistingLevel(existingItem, path, filename, item, stoppingToken);
        }

        logger.LogInformation("Received item isn't newer than the existing item, skipping");
        return Result.Ok(false);
    }

    private async Task<Result<int>> CreateNewLevel(
        string path,
        string filename,
        PublishedFileDetails item,
        CancellationToken stoppingToken
    )
    {
        string[] lines = await File.ReadAllLinesAsync(path, stoppingToken);
        if (lines.Length == 0)
        {
            return Result.Fail(new ExceptionalError(new InvalidDataException("Level file is empty")));
        }

        string[] splits = lines[0].Split(',');
        string author = splits[1];
        string uid = splits[2];

        if (string.IsNullOrEmpty(author) || string.IsNullOrWhiteSpace(author))
        {
            logger.LogWarning("Author for {Filename} ({WorkshopId}) is empty", filename, item.PublishedFileId);
            author = "[Unknown]";
        }

        ParseTimes(filename,
            item,
            lines[2].Split(','),
            out bool valid,
            out float parsedValidation,
            out float parsedGold,
            out float parsedSilver,
            out float parsedBronze);

        string hash = Hash(await GetTextToHash(path, stoppingToken));
        string sourceDirectory = Path.GetDirectoryName(path)!;
        string? image = Directory.GetFiles(sourceDirectory, "*.jpg").FirstOrDefault();

        if (string.IsNullOrEmpty(image))
        {
            logger.LogWarning("No image found for {Filename}", filename);
        }

        using (FileStream zipStream = File.Create(path + ".zip"))
        {
            string filePath = Path.Combine(sourceDirectory, filename + ".zeeplevel");
            using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(filePath, filename + ".zeeplevel", CompressionLevel.Optimal);
            }
        }

        string identifier = Guid.NewGuid().ToString();

        logger.LogInformation("Identifier: {Identifier}", identifier);

        Result<string> uploadLevelResult =
            await uploadService.UploadLevel(identifier,
                await File.ReadAllBytesAsync(path + ".zip", stoppingToken),
                stoppingToken);

        if (uploadLevelResult.IsFailed)
        {
            return uploadLevelResult.ToResult();
        }

        Result<string> uploadThumbnailResult;

        if (!string.IsNullOrEmpty(image))
        {
            uploadThumbnailResult = await uploadService.UploadThumbnail(identifier,
                await File.ReadAllBytesAsync(image, stoppingToken),
                stoppingToken);

            if (uploadThumbnailResult.IsFailed)
            {
                return uploadThumbnailResult.ToResult();
            }
        }
        else
        {
            uploadThumbnailResult = "https://storage.googleapis.com/zworpshop/image-not-found.png";
        }

        Result<LevelResponseModel> createLevelResult = await apiClient.CreateLevel(builder =>
        {
            builder
                .WithWorkshopId(item.PublishedFileId)
                .WithAuthorId(item.Creator)
                .WithName(filename)
                .WithCreatedAt(item.TimeCreated)
                .WithUpdatedAt(item.TimeUpdated)
                .WithImageUrl(uploadThumbnailResult.Value)
                .WithFileUrl(uploadLevelResult.Value)
                .WithFileUid(uid)
                .WithFileHash(hash)
                .WithFileAuthor(author)
                .WithValid(valid)
                .WithValidation(parsedValidation)
                .WithGold(parsedGold)
                .WithSilver(parsedSilver)
                .WithBronze(parsedBronze);
        });

        if (createLevelResult.IsFailed)
        {
            return createLevelResult.ToResult();
        }

        logger.LogInformation("Created level [{LevelId} ({WorkshopId})] {Name} by {Author} ({AuthorId})",
            createLevelResult.Value.Id,
            item.PublishedFileId,
            filename,
            author,
            item.Creator);

        return createLevelResult.Value.Id;
    }

    private void ParseTimes(
        string filename,
        PublishedFileDetails item,
        string[] splits,
        out bool valid,
        out float parsedValidation,
        out float parsedGold,
        out float parsedSilver,
        out float parsedBronze
    )
    {
        parsedValidation = 0;
        parsedGold = 0;
        parsedSilver = 0;
        parsedBronze = 0;

        valid = false;

        if (splits.Length >= 4)
        {
            valid = float.TryParse(splits[0], out parsedValidation) &&
                    float.TryParse(splits[1], out parsedGold) &&
                    float.TryParse(splits[2], out parsedSilver) &&
                    float.TryParse(splits[3], out parsedBronze);
        }
        else
        {
            logger.LogWarning("Not enough splits for {Filename} ({WorkshopId})", filename, item.PublishedFileId);
        }

        if (valid)
        {
            if (float.IsNaN(parsedValidation) || float.IsInfinity(parsedValidation) ||
                float.IsNaN(parsedGold) || float.IsInfinity(parsedGold) ||
                float.IsNaN(parsedSilver) || float.IsInfinity(parsedSilver) ||
                float.IsNaN(parsedBronze) || float.IsInfinity(parsedBronze))
            {
                valid = false;
            }
        }

        if (!valid)
        {
            parsedValidation = 0;
            parsedGold = 0;
            parsedSilver = 0;
            parsedBronze = 0;
        }
    }

    private async Task<Result<bool>> ReplaceExistingLevel(
        LevelResponseModel existingItem,
        string path,
        string filename,
        PublishedFileDetails item,
        CancellationToken stoppingToken
    )
    {
        int existingId = existingItem.Id;
        string newUid = await GetUidFromFile(path, stoppingToken);

        if (string.Equals(existingItem.FileUid, newUid))
        {
            if (existingItem.UpdatedAt == item.TimeUpdated)
            {
                logger.LogInformation("False positive for {Filename} ({WorkshopId})", filename, item.PublishedFileId);
                return Result.Ok(false);
            }

            if (existingItem.UpdatedAt < item.TimeUpdated)
            {
                string textToHash = await GetTextToHash(path, stoppingToken);
                string newFileHash = Hash(textToHash);

                if (newFileHash == existingItem.FileHash)
                {
                    Result<LevelResponseModel> updateResult = await apiClient.UpdateLevelTime(existingId,
                        new DateTimeOffset(item.TimeUpdated).ToUnixTimeSeconds());

                    return updateResult.IsSuccess ? Result.Ok(false) : updateResult.ToResult();
                }

                logger.LogError("Hashes don't match for {Filename} ({WorkshopId})", filename, item.PublishedFileId);
                return Result.Ok(false);
            }

            if (existingItem.UpdatedAt > item.TimeUpdated)
            {
                logger.LogInformation("False positive for {Filename} ({WorkshopId}), ours is newer somehow",
                    filename,
                    item.PublishedFileId);
                return Result.Ok(false);
            }
        }

        int newId;

        try
        {
            Result<int> createResult = await CreateNewLevel(path, filename, item, stoppingToken);
            if (createResult.IsFailed)
                return createResult.ToResult();

            newId = createResult.Value;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unable to create new level");
            return Result.Fail(new ExceptionalError(e));
        }

        Result<LevelResponseModel> result = await apiClient.ReplaceLevel(existingId, newId);

        if (result.IsFailed)
        {
            logger.LogCritical("Unable to replace level {ExistingId} with {NewId}; Result: {Result}",
                existingId,
                newId,
                result.ToString());

            return result.ToResult();
        }

        logger.LogInformation("Replaced level {ExistingId} with {NewId}", existingId, newId);
        return Result.Ok(true);
    }

    private static async Task<string> GetUidFromFile(string path, CancellationToken stoppingToken)
    {
        string[] lines = await File.ReadAllLinesAsync(path, stoppingToken);
        return lines[0].Split(',')[2];
    }

    private static async Task<string> GetTextToHash(string path, CancellationToken stoppingToken)
    {
        string[] lines = await File.ReadAllLinesAsync(path, stoppingToken);
        string[] splits = lines[2].Split(',');

        string skyboxAndBasePlate = splits.Length != 6
            ? "unknown,unknown"
            : splits[^2] + "," + splits[^1];

        return string.Join("\n", lines.Skip(3).Prepend(skyboxAndBasePlate));
    }

    private static string Hash(string input)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        StringBuilder sb = new(hash.Length * 2);

        foreach (byte b in hash)
        {
            // can be "x2" if you want lowercase
            sb.Append(b.ToString("X2"));
        }

        return sb.ToString();
    }
}
