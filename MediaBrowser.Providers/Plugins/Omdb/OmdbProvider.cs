#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Json;
using MediaBrowser.Common.Json.Converters;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace MediaBrowser.Providers.Plugins.Omdb
{
    public class OmdbProvider
    {
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        private readonly IApplicationHost _appHost;
        private readonly JsonSerializerOptions _jsonOptions;

        public OmdbProvider(IHttpClientFactory httpClientFactory, IFileSystem fileSystem, IApplicationHost appHost, IServerConfigurationManager configurationManager)
        {
            _httpClientFactory = httpClientFactory;
            _fileSystem = fileSystem;
            _configurationManager = configurationManager;
            _appHost = appHost;

            _jsonOptions = new JsonSerializerOptions(JsonDefaults.GetOptions());
            _jsonOptions.Converters.Add(new JsonOmdbNotAvailableStringConverter());
            _jsonOptions.Converters.Add(new JsonOmdbNotAvailableInt32Converter());
        }

        public async Task Fetch<T>(MetadataResult<T> itemResult, string imdbId, string language, string country, CancellationToken cancellationToken)
            where T : BaseItem
        {
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var item = itemResult.Item;

            var result = await GetRootObject(imdbId, cancellationToken).ConfigureAwait(false);

            // Only take the name and rating if the user's language is set to English, since Omdb has no localization
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) || _configurationManager.Configuration.EnableNewOmdbSupport)
            {
                item.Name = result.Title;

                if (string.Equals(country, "us", StringComparison.OrdinalIgnoreCase))
                {
                    item.OfficialRating = result.Rated;
                }
            }

            if (!string.IsNullOrEmpty(result.Year) && result.Year.Length >= 4
                && int.TryParse(result.Year.AsSpan().Slice(0, 4), NumberStyles.Number, _usCulture, out var year)
                && year >= 0)
            {
                item.ProductionYear = year;
            }

            var tomatoScore = result.GetRottenTomatoScore();

            if (tomatoScore.HasValue)
            {
                item.CriticRating = tomatoScore;
            }

            if (!string.IsNullOrEmpty(result.imdbVotes)
                && int.TryParse(result.imdbVotes, NumberStyles.Number, _usCulture, out var voteCount)
                && voteCount >= 0)
            {
                // item.VoteCount = voteCount;
            }

            if (!string.IsNullOrEmpty(result.imdbRating)
                && float.TryParse(result.imdbRating, NumberStyles.Any, _usCulture, out var imdbRating)
                && imdbRating >= 0)
            {
                item.CommunityRating = imdbRating;
            }

            if (!string.IsNullOrEmpty(result.Website))
            {
                item.HomePageUrl = result.Website;
            }

            if (!string.IsNullOrWhiteSpace(result.imdbID))
            {
                item.SetProviderId(MetadataProvider.Imdb, result.imdbID);
            }

            ParseAdditionalMetadata(itemResult, result);
        }

        public async Task<bool> FetchEpisodeData<T>(MetadataResult<T> itemResult, int episodeNumber, int seasonNumber, string episodeImdbId, string seriesImdbId, string language, string country, CancellationToken cancellationToken)
            where T : BaseItem
        {
            if (string.IsNullOrWhiteSpace(seriesImdbId))
            {
                throw new ArgumentNullException(nameof(seriesImdbId));
            }

            var item = itemResult.Item;

            var seasonResult = await GetSeasonRootObject(seriesImdbId, seasonNumber, cancellationToken).ConfigureAwait(false);

            if (seasonResult?.Episodes == null)
            {
                return false;
            }

            RootObject result = null;

            if (!string.IsNullOrWhiteSpace(episodeImdbId))
            {
                foreach (var episode in seasonResult.Episodes)
                {
                    if (string.Equals(episodeImdbId, episode.imdbID, StringComparison.OrdinalIgnoreCase))
                    {
                        result = episode;
                        break;
                    }
                }
            }

            // finally, search by numbers
            if (result == null)
            {
                foreach (var episode in seasonResult.Episodes)
                {
                    if (episode.Episode == episodeNumber)
                    {
                        result = episode;
                        break;
                    }
                }
            }

            if (result == null)
            {
                return false;
            }

            // Only take the name and rating if the user's language is set to English, since Omdb has no localization
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) || _configurationManager.Configuration.EnableNewOmdbSupport)
            {
                item.Name = result.Title;

                if (string.Equals(country, "us", StringComparison.OrdinalIgnoreCase))
                {
                    item.OfficialRating = result.Rated;
                }
            }

            if (!string.IsNullOrEmpty(result.Year) && result.Year.Length >= 4
                && int.TryParse(result.Year.AsSpan().Slice(0, 4), NumberStyles.Number, _usCulture, out var year)
                && year >= 0)
            {
                item.ProductionYear = year;
            }

            var tomatoScore = result.GetRottenTomatoScore();

            if (tomatoScore.HasValue)
            {
                item.CriticRating = tomatoScore;
            }

            if (!string.IsNullOrEmpty(result.imdbVotes)
                && int.TryParse(result.imdbVotes, NumberStyles.Number, _usCulture, out var voteCount)
                && voteCount >= 0)
            {
                // item.VoteCount = voteCount;
            }

            if (!string.IsNullOrEmpty(result.imdbRating)
                && float.TryParse(result.imdbRating, NumberStyles.Any, _usCulture, out var imdbRating)
                && imdbRating >= 0)
            {
                item.CommunityRating = imdbRating;
            }

            if (!string.IsNullOrEmpty(result.Website))
            {
                item.HomePageUrl = result.Website;
            }

            if (!string.IsNullOrWhiteSpace(result.imdbID))
            {
                item.SetProviderId(MetadataProvider.Imdb, result.imdbID);
            }

            ParseAdditionalMetadata(itemResult, result);

            return true;
        }

        internal async Task<RootObject> GetRootObject(string imdbId, CancellationToken cancellationToken)
        {
            var path = await EnsureItemInfo(imdbId, cancellationToken).ConfigureAwait(false);
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<RootObject>(stream, _jsonOptions, cancellationToken);
        }

        internal async Task<SeasonRootObject> GetSeasonRootObject(string imdbId, int seasonId, CancellationToken cancellationToken)
        {
            var path = await EnsureSeasonInfo(imdbId, seasonId, cancellationToken).ConfigureAwait(false);
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SeasonRootObject>(stream, _jsonOptions, cancellationToken);
        }

        internal static bool IsValidSeries(Dictionary<string, string> seriesProviderIds)
        {
            if (seriesProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out string id) && !string.IsNullOrEmpty(id))
            {
                // This check should ideally never be necessary but we're seeing some cases of this and haven't tracked them down yet.
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return true;
                }
            }

            return false;
        }

        public static string GetOmdbUrl(string query)
        {
            const string Url = "https://www.omdbapi.com?apikey=2c9d9507";

            if (string.IsNullOrWhiteSpace(query))
            {
                return Url;
            }

            return Url + "&" + query;
        }

        private async Task<string> EnsureItemInfo(string imdbId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var imdbParam = imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdbId : "tt" + imdbId;

            var path = GetDataFilePath(imdbParam);

            var fileInfo = _fileSystem.GetFileSystemInfo(path);

            if (fileInfo.Exists)
            {
                // If it's recent or automatic updates are enabled, don't re-download
                if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 1)
                {
                    return path;
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
            }

            var url = GetOmdbUrl(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "i={0}&plot=short&tomatoes=true&r=json",
                    imdbParam));

            var rootObject = await GetDeserializedOmdbResponse<RootObject>(_httpClientFactory.CreateClient(NamedClient.Default), url, cancellationToken).ConfigureAwait(false);
            await using FileStream jsonFileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(jsonFileStream, rootObject, _jsonOptions, cancellationToken).ConfigureAwait(false);

            return path;
        }

        private async Task<string> EnsureSeasonInfo(string seriesImdbId, int seasonId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(seriesImdbId))
            {
                throw new ArgumentException("The series IMDb ID was null or whitespace.", nameof(seriesImdbId));
            }

            var imdbParam = seriesImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? seriesImdbId : "tt" + seriesImdbId;

            var path = GetSeasonFilePath(imdbParam, seasonId);

            var fileInfo = _fileSystem.GetFileSystemInfo(path);

            if (fileInfo.Exists)
            {
                // If it's recent or automatic updates are enabled, don't re-download
                if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 1)
                {
                    return path;
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
            }

            var url = GetOmdbUrl(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "i={0}&season={1}&detail=full",
                    imdbParam,
                    seasonId));

            var rootObject = await GetDeserializedOmdbResponse<SeasonRootObject>(_httpClientFactory.CreateClient(NamedClient.Default), url, cancellationToken).ConfigureAwait(false);
            await using FileStream jsonFileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(jsonFileStream, rootObject, _jsonOptions, cancellationToken).ConfigureAwait(false);

            return path;
        }

        public async Task<T> GetDeserializedOmdbResponse<T>(HttpClient httpClient, string url, CancellationToken cancellationToken)
        {
            using var response = await GetOmdbResponse(httpClient, url, cancellationToken).ConfigureAwait(false);
            await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return await JsonSerializer.DeserializeAsync<T>(content, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        public static Task<HttpResponseMessage> GetOmdbResponse(HttpClient httpClient, string url, CancellationToken cancellationToken)
        {
            return httpClient.GetAsync(url, cancellationToken);
        }

        internal string GetDataFilePath(string imdbId)
        {
            if (string.IsNullOrEmpty(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var dataPath = Path.Combine(_configurationManager.ApplicationPaths.CachePath, "omdb");

            var filename = string.Format(CultureInfo.InvariantCulture, "{0}.json", imdbId);

            return Path.Combine(dataPath, filename);
        }

        internal string GetSeasonFilePath(string imdbId, int seasonId)
        {
            if (string.IsNullOrEmpty(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var dataPath = Path.Combine(_configurationManager.ApplicationPaths.CachePath, "omdb");

            var filename = string.Format(CultureInfo.InvariantCulture, "{0}_season_{1}.json", imdbId, seasonId);

            return Path.Combine(dataPath, filename);
        }

        private void ParseAdditionalMetadata<T>(MetadataResult<T> itemResult, RootObject result)
            where T : BaseItem
        {
            var item = itemResult.Item;

            var isConfiguredForEnglish = IsConfiguredForEnglish(item) || _configurationManager.Configuration.EnableNewOmdbSupport;

            // Grab series genres because IMDb data is better than TVDB. Leave movies alone
            // But only do it if English is the preferred language because this data will not be localized
            if (isConfiguredForEnglish && !string.IsNullOrWhiteSpace(result.Genre))
            {
                item.Genres = Array.Empty<string>();

                foreach (var genre in result.Genre
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim())
                    .Where(i => !string.IsNullOrWhiteSpace(i)))
                {
                    item.AddGenre(genre);
                }
            }

            if (isConfiguredForEnglish)
            {
                // Omdb is currently English only, so for other languages skip this and let secondary providers fill it in
                item.Overview = result.Plot;
            }

            if (!Plugin.Instance.Configuration.CastAndCrew)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Director))
            {
                var person = new PersonInfo
                {
                    Name = result.Director.Trim(),
                    Type = PersonType.Director
                };

                itemResult.AddPerson(person);
            }

            if (!string.IsNullOrWhiteSpace(result.Writer))
            {
                var person = new PersonInfo
                {
                    Name = result.Writer.Trim(),
                    Type = PersonType.Writer
                };

                itemResult.AddPerson(person);
            }

            if (!string.IsNullOrWhiteSpace(result.Actors))
            {
                var actorList = result.Actors.Split(',');
                foreach (var actor in actorList)
                {
                    if (!string.IsNullOrWhiteSpace(actor))
                    {
                        var person = new PersonInfo
                        {
                            Name = actor.Trim(),
                            Type = PersonType.Actor
                        };

                        itemResult.AddPerson(person);
                    }
                }
            }
        }

        private bool IsConfiguredForEnglish(BaseItem item)
        {
            var lang = item.GetPreferredMetadataLanguage();

            // The data isn't localized and so can only be used for English users
            return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        }

        internal class SeasonRootObject
        {
            public string Title { get; set; }

            public string seriesID { get; set; }

            public int? Season { get; set; }

            public int? totalSeasons { get; set; }

            public RootObject[] Episodes { get; set; }

            public string Response { get; set; }
        }

        internal class RootObject
        {
            public string Title { get; set; }

            public string Year { get; set; }

            public string Rated { get; set; }

            public string Released { get; set; }

            public string Runtime { get; set; }

            public string Genre { get; set; }

            public string Director { get; set; }

            public string Writer { get; set; }

            public string Actors { get; set; }

            public string Plot { get; set; }

            public string Language { get; set; }

            public string Country { get; set; }

            public string Awards { get; set; }

            public string Poster { get; set; }

            public List<OmdbRating> Ratings { get; set; }

            public string Metascore { get; set; }

            public string imdbRating { get; set; }

            public string imdbVotes { get; set; }

            public string imdbID { get; set; }

            public string Type { get; set; }

            public string DVD { get; set; }

            public string BoxOffice { get; set; }

            public string Production { get; set; }

            public string Website { get; set; }

            public string Response { get; set; }

            public int? Episode { get; set; }

            public float? GetRottenTomatoScore()
            {
                if (Ratings != null)
                {
                    var rating = Ratings.FirstOrDefault(i => string.Equals(i.Source, "Rotten Tomatoes", StringComparison.OrdinalIgnoreCase));
                    if (rating != null && rating.Value != null)
                    {
                        var value = rating.Value.TrimEnd('%');
                        if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var score))
                        {
                            return score;
                        }
                    }
                }

                return null;
            }
        }

        public class OmdbRating
        {
            public string Source { get; set; }

            public string Value { get; set; }
        }
    }
}
