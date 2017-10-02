﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.OData;
using System.Web.Http.OData.Query;
using NuGetGallery.Configuration;
using NuGetGallery.OData;
using NuGetGallery.OData.QueryInterceptors;
using NuGetGallery.WebApi;
using QueryInterceptor;
using WebApi.OutputCache.V2;

// ReSharper disable once CheckNamespace
namespace NuGetGallery.Controllers
{
    public class ODataV2CuratedFeedController
        : NuGetODataController
    {
        private const int MaxPageSize = 40;

        private readonly IEntitiesContext _entities;
        private readonly IGalleryConfigurationService _configurationService;
        private readonly ISearchService _searchService;
        private readonly ICuratedFeedService _curatedFeedService;

        public ODataV2CuratedFeedController(
            IEntitiesContext entities,
            IGalleryConfigurationService configurationService,
            ISearchService searchService,
            ICuratedFeedService curatedFeedService)
            : base(configurationService)
        {
            _entities = entities;
            _configurationService = configurationService;
            _searchService = searchService;
            _curatedFeedService = curatedFeedService;
        }

        // /api/v2/curated-feed/curatedFeedName/Packages?semVerLevel=
        [HttpGet]
        [HttpPost]
        [CacheOutput(NoCache = true)]
        public IHttpActionResult Get(
            ODataQueryOptions<V2FeedPackage> options,
            string curatedFeedName,
            [FromUri] string semVerLevel = null)
        {
            if (!_entities.CuratedFeeds.Any(cf => cf.Name == curatedFeedName))
            {
                return NotFound();
            }

            var semVerLevelKey = SemVerLevelKey.ForSemVerLevel(semVerLevel);

            var queryable = _curatedFeedService
                .GetPackages(curatedFeedName)
                .Where(p => p.PackageStatusKey == PackageStatus.Available)
                .Where(SemVerLevelKey.IsPackageCompliantWithSemVerLevelPredicate(semVerLevel))
                .ToV2FeedPackageQuery(
                    _configurationService.GetSiteRoot(UseHttps()),
                    _configurationService.Features.FriendlyLicenses, 
                    semVerLevelKey)
                .InterceptWith(new NormalizeVersionInterceptor());

            return QueryResult(options, queryable, MaxPageSize);
        }

        // /api/v2/curated-feed/curatedFeedName/Packages/$count?semVerLevel=
        [HttpGet]
        [CacheOutput(NoCache = true)]
        public IHttpActionResult GetCount(
            ODataQueryOptions<V2FeedPackage> options,
            string curatedFeedName,
            [FromUri] string semVerLevel = null)
        {
            return Get(options, curatedFeedName, semVerLevel).FormattedAsCountResult<V2FeedPackage>();
        }

        // /api/v2/curated-feed/curatedFeedName/Packages(Id=,Version=)
        [HttpGet]
        [CacheOutput(ServerTimeSpan = NuGetODataConfig.GetByIdAndVersionCacheTimeInSeconds, Private = true, ClientTimeSpan = NuGetODataConfig.GetByIdAndVersionCacheTimeInSeconds)]
        public async Task<IHttpActionResult> Get(ODataQueryOptions<V2FeedPackage> options, string curatedFeedName, string id, string version)
        {
            var result = await GetCore(options, curatedFeedName, id, version, return404NotFoundWhenNoResults: true, semVerLevel: SemVerLevelKey.SemVerLevel2);
            return result.FormattedAsSingleResult<V2FeedPackage>();
        }

        // /api/v2/curated-feed/curatedFeedName/FindPackagesById()?id=&semVerLevel=
        [HttpGet]
        [HttpPost]
        [CacheOutput(ServerTimeSpan = NuGetODataConfig.GetByIdAndVersionCacheTimeInSeconds, Private = true, ClientTimeSpan = NuGetODataConfig.GetByIdAndVersionCacheTimeInSeconds)]
        public async Task<IHttpActionResult> FindPackagesById(
            ODataQueryOptions<V2FeedPackage> options,
            string curatedFeedName,
            [FromODataUri] string id,
            [FromUri] string semVerLevel = null)
        {
            if (string.IsNullOrEmpty(curatedFeedName) || string.IsNullOrEmpty(id))
            {
                var semVerLevelKey = SemVerLevelKey.ForSemVerLevel(semVerLevel);

                var emptyResult = Enumerable.Empty<Package>().AsQueryable()
                    .ToV2FeedPackageQuery(GetSiteRoot(), _configurationService.Features.FriendlyLicenses, semVerLevelKey);

                return QueryResult(options, emptyResult, MaxPageSize);
            }

            return await GetCore(options, curatedFeedName, id, normalizedVersion: null, return404NotFoundWhenNoResults: false, semVerLevel: semVerLevel);
        }

        // /api/v2/curated-feed/curatedFeedName/FindPackagesById()/$count?id=&semVerLevel=
        [HttpGet]
        [CacheOutput(NoCache = true)]
        public async Task<IHttpActionResult> FindPackagesByIdCount(
            ODataQueryOptions<V2FeedPackage> options,
            string curatedFeedName,
            [FromODataUri] string id,
            [FromUri] string semVerLevel = null)
        {
            var result = await FindPackagesById(options, curatedFeedName, id, semVerLevel);
            return result.FormattedAsCountResult<V2FeedPackage>();
        }

        private async Task<IHttpActionResult> GetCore(
            ODataQueryOptions<V2FeedPackage> options,
            string curatedFeedName,
            string id,
            string normalizedVersion,
            bool return404NotFoundWhenNoResults,
            string semVerLevel)
        {
            var curatedFeed = _entities.CuratedFeeds.FirstOrDefault(cf => cf.Name == curatedFeedName);
            if (curatedFeed == null)
            {
                return NotFound();
            }

            var packages = _curatedFeedService
                .GetPackages(curatedFeedName)
                .Where(p => p.PackageStatusKey == PackageStatus.Available)
                .Where(SemVerLevelKey.IsPackageCompliantWithSemVerLevelPredicate(semVerLevel))
                .Where(p => p.PackageRegistration.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(normalizedVersion))
            {
                packages = packages.Where(p => p.NormalizedVersion == normalizedVersion);
            }

            var semVerLevelKey = SemVerLevelKey.ForSemVerLevel(semVerLevel);

            // try the search service
            try
            {
                var searchAdaptorResult = await SearchAdaptor.FindByIdAndVersionCore(
                    _searchService,
                    GetTraditionalHttpContext().Request,
                    packages,
                    id,
                    normalizedVersion,
                    curatedFeed: curatedFeed,
                    semVerLevel: semVerLevel);

                // If intercepted, create a paged queryresult
                if (searchAdaptorResult.ResultsAreProvidedBySearchService)
                {
                    // Packages provided by search service
                    packages = searchAdaptorResult.Packages;

                    // Add explicit Take() needed to limit search hijack result set size if $top is specified
                    var totalHits = packages.LongCount();

                    if (return404NotFoundWhenNoResults && totalHits == 0)
                    {
                        return NotFound();
                    }

                    var pagedQueryable = packages
                        .Take(options.Top != null ? Math.Min(options.Top.Value, MaxPageSize) : MaxPageSize)
                        .ToV2FeedPackageQuery(GetSiteRoot(), _configurationService.Features.FriendlyLicenses, semVerLevelKey);

                    return QueryResult(options, pagedQueryable, MaxPageSize, totalHits, (o, s, resultCount) =>
                       SearchAdaptor.GetNextLink(Request.RequestUri, resultCount, new { id }, o, s));
                }
            }
            catch (Exception ex)
            {
                // Swallowing Exception intentionally. If *anything* goes wrong in search, just fall back to the database.
                // We don't want to break package restores. We do want to know if this happens, so here goes:
                QuietLog.LogHandledException(ex);
            }

            if (return404NotFoundWhenNoResults && !packages.Any())
            {
                return NotFound();
            }

            var queryable = packages.ToV2FeedPackageQuery(
                GetSiteRoot(), 
                _configurationService.Features.FriendlyLicenses, 
                semVerLevelKey);

            return QueryResult(options, queryable, MaxPageSize);
        }

        // /api/v2/curated-feed/curatedFeedName/Packages(Id=,Version=)/propertyName
        [HttpGet]
        public IHttpActionResult GetPropertyFromPackages(string propertyName, string id, string version)
        {
            switch (propertyName.ToLowerInvariant())
            {
                case "id": return Ok(id);
                case "version": return Ok(version);
            }

            return BadRequest("Querying property " + propertyName + " is not supported.");
        }

        // /api/v2/curated-feed/curatedFeedName/Search()?searchTerm=&targetFramework=&includePrerelease=&semVerLevel=
        [HttpGet]
        [HttpPost]
        [CacheOutput(ServerTimeSpan = NuGetODataConfig.SearchCacheTimeInSeconds, ClientTimeSpan = NuGetODataConfig.SearchCacheTimeInSeconds)]
        public async Task<IHttpActionResult> Search(
            ODataQueryOptions<V2FeedPackage> options,
            string curatedFeedName,
            [FromODataUri]string searchTerm = "",
            [FromODataUri]string targetFramework = "",
            [FromODataUri]bool includePrerelease = false,
            [FromUri]string semVerLevel = null)
        {
            if (!_entities.CuratedFeeds.Any(cf => cf.Name == curatedFeedName))
            {
                return NotFound();
            }

            // Handle OData-style |-separated list of frameworks.
            string[] targetFrameworkList = (targetFramework ?? "").Split(new[] { '\'', '|' }, StringSplitOptions.RemoveEmptyEntries);

            // For now, we'll just filter on the first one.
            if (targetFrameworkList.Length > 0)
            {
                // Until we support multiple frameworks, we need to prefer aspnet50 over aspnetcore50.
                if (targetFrameworkList.Contains("aspnet50"))
                {
                    targetFramework = "aspnet50";
                }
                else
                {
                    targetFramework = targetFrameworkList[0];
                }
            }

            // Perform actual search
            var curatedFeed = _curatedFeedService.GetFeedByName(curatedFeedName, includePackages: false);
            var packages = _curatedFeedService.GetPackages(curatedFeedName)
                .Where(p => p.PackageStatusKey == PackageStatus.Available)
                .Where(SemVerLevelKey.IsPackageCompliantWithSemVerLevelPredicate(semVerLevel))
                .OrderBy(p => p.PackageRegistration.Id).ThenBy(p => p.Version);

            // todo: search hijack should take queryOptions instead of manually parsing query options
            var searchAdaptorResult = await SearchAdaptor.SearchCore(
                _searchService,
                GetTraditionalHttpContext().Request,
                packages,
                searchTerm,
                targetFramework,
                includePrerelease,
                curatedFeed: curatedFeed,
                semVerLevel: semVerLevel);

            // Packages provided by search service (even when not hijacked)
            var query = searchAdaptorResult.Packages;

            var semVerLevelKey = SemVerLevelKey.ForSemVerLevel(semVerLevel);

            // If intercepted, create a paged queryresult
            if (searchAdaptorResult.ResultsAreProvidedBySearchService)
            {
                // Add explicit Take() needed to limit search hijack result set size if $top is specified
                var totalHits = query.LongCount();
                var pagedQueryable = query
                    .Take(options.Top != null ? Math.Min(options.Top.Value, MaxPageSize) : MaxPageSize)
                    .ToV2FeedPackageQuery(
                        GetSiteRoot(), 
                        _configurationService.Features.FriendlyLicenses, 
                        semVerLevelKey);

                return QueryResult(options, pagedQueryable, MaxPageSize, totalHits, (o, s, resultCount) =>
                {
                    // The nuget.exe 2.x list command does not like the next link at the bottom when a $top is passed.
                    // Strip it of for backward compatibility.
                    if (o.Top == null || (resultCount.HasValue && o.Top.Value >= resultCount.Value))
                    {
                        return SearchAdaptor.GetNextLink(
                            Request.RequestUri, 
                            resultCount, 
                            new { searchTerm, targetFramework, includePrerelease }, 
                            o, 
                            s);
                    }
                    return null;
                });
            }

            // If not, just let OData handle things
            var queryable = query.ToV2FeedPackageQuery(
                GetSiteRoot(), 
                _configurationService.Features.FriendlyLicenses, 
                semVerLevelKey);

            return QueryResult(options, queryable, MaxPageSize);
        }

        // /api/v2/curated-feed/curatedFeedName/Search()/$count?searchTerm=&targetFramework=&includePrerelease=
        [HttpGet]
        [CacheOutput(ServerTimeSpan = NuGetODataConfig.SearchCacheTimeInSeconds, ClientTimeSpan = NuGetODataConfig.SearchCacheTimeInSeconds)]
        public async Task<IHttpActionResult> SearchCount(
            ODataQueryOptions<V2FeedPackage> options,
            string curatedFeedName,
            [FromODataUri]string searchTerm = "",
            [FromODataUri]string targetFramework = "",
            [FromODataUri]bool includePrerelease = false,
            [FromUri]string semVerLevel = null)
        {
            var searchResults = await Search(options, curatedFeedName, searchTerm, targetFramework, includePrerelease, semVerLevel);
            return searchResults.FormattedAsCountResult<V2FeedPackage>();
        }
    }
}