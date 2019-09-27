﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Search;
using SearchScorer.Common;

namespace SearchScorer
{
    class Program
    {
        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = 64;

            var assemblyDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            var settings = new SearchScorerSettings
            {
                ControlBaseUrl = "https://api-v2v3search-0.nuget.org/",
                TreatmentBaseUrl = "https://azuresearch-usnc.nuget.org/",
                FeedbackSearchQueriesCsvPath = Path.Combine(assemblyDir, "FeedbackSearchQueries.csv"),
                CuratedSearchQueriesCsvPath = Path.Combine(assemblyDir, "CuratedSearchQueries.csv"),
                TopSearchQueriesCsvPath = @"C:\Users\jver\Desktop\search-scorer\TopSearchQueries-2019-08-05.csv",
                TopSearchSelectionsCsvPath = @"C:\Users\jver\Desktop\search-scorer\TopSearchSelections-2019-08-05.csv",
                TopSearchSelectionsV2CsvPath = @"C:\Users\jver\Desktop\search-scorer\TopSearchSelectionsV2-2019-08-05.csv",
                GoogleAnalyticsSearchReferralsCsvPath = @"C:\Users\jver\Desktop\search-scorer\GoogleAnalyticsSearchReferrals-2019-07-03-2019-08-04.csv",
                GitHubUsageJsonPath = @"C:\Users\jver\Desktop\search-scorer\GitHubUsage.v1-2019-08-06.json",
                GitHubUsageCsvPath = @"C:\Users\jver\Desktop\search-scorer\GitHubUsage.v1-2019-08-06.csv",

                // The following settings are only necessary if running the "probe" command.
                AzureSearchServiceName = "",
                AzureSearchIndexName = "",
                AzureSearchApiKey = "",
                ProbeResultsCsvPath = @"C:\Users\jver\Desktop\search-scorer\ProbeResults.csv",
            };

            // WriteConvenientCsvs(settings);

            using (var httpClientHandler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip })
            using (var httpClient = new HttpClient())
            {
                var searchClient = new SearchClient(httpClient);
                var scoreEvaluator = new IREvalutation.RelevancyScoreEvaluator(searchClient);

                if (args.Length == 0 || args[0] == "score")
                {
                    // await VerifyPackageIdsExistAsync(settings, searchClient);
                    await RunScoreCommandAsync(settings, scoreEvaluator);
                }
                else if (args[0] == "probe")
                {
                    await RunProbeCommandAsync(settings, scoreEvaluator);
                }
            }
        }

        private static async Task RunScoreCommandAsync(
            SearchScorerSettings settings,
            IREvalutation.RelevancyScoreEvaluator scoreEvaluator)
        {
            await scoreEvaluator.RunAsync(settings);
        }

        private static async Task RunProbeCommandAsync(
            SearchScorerSettings settings,
            IREvalutation.RelevancyScoreEvaluator scoreEvaluator)
        {
            var credentials = new SearchCredentials(settings.AzureSearchApiKey);
            var azureSearchClient = new SearchServiceClient(settings.AzureSearchServiceName, credentials);

            var index = await azureSearchClient.GetNuGetSearchIndexAsync(settings);

            var results = new List<SearchProbesRecord>();
            foreach (var test in GetProbeTests())
            {
                // Update the Azure Search index
                await azureSearchClient.UpdateNuGetSearchIndexAsync(
                    settings,
                    index,
                    test.PackageIdWeight,
                    test.TokenizedPackageIdWeight,
                    test.TagsWeight,
                    test.DownloadScoreBoost);

                // Score the new index.
                var report = await scoreEvaluator.GetCustomVariantReportAsync(
                    settings,
                    customVariantUrl: settings.TreatmentBaseUrl);

                results.Add(new SearchProbesRecord
                {
                    PackageIdWeight = test.PackageIdWeight,
                    TokenizedPackageIdWeight = test.TokenizedPackageIdWeight,
                    TagsWeight = test.TagsWeight,
                    DownloadScoreBoost = test.DownloadScoreBoost,

                    CuratedSearchScore = report.CuratedSearchQueries.Score,
                    FeedbackScore = report.FeedbackSearchQueries.Score
                });
            }

            SearchProbesCsvWriter.Write(settings.ProbeResultsCsvPath, results);
        }

        private static IEnumerable<SearchProbeTest> GetProbeTests()
        {
            var packageIdWeights = CreateRange(lower: 1, upper: 10, increments: 1);
            var tokenizedPackageIdWeights = CreateRange(lower: 1, upper: 10, increments: 1);
            var tagWeights = CreateRange(lower: 1, upper: 10, increments: 1);
            var downloadWeights = CreateRange(lower: 1000, upper: 30000, increments: 1000);

            return CartesianProduct(new[] { packageIdWeights, tokenizedPackageIdWeights, tagWeights, downloadWeights })
                .Select(x =>
                {
                    var values = x.ToList();

                    return new SearchProbeTest
                    {
                        PackageIdWeight = values[0],
                        TokenizedPackageIdWeight = values[1],
                        TagsWeight = values[2],
                        DownloadScoreBoost = values[3]
                    };
                });
        }

        private static IEnumerable<double> CreateRange(int lower, int upper, double increments)
        {
            var count = (upper - lower) / increments + 1;
            return Enumerable
                .Range(0, (int)count)
                .Select(i => (i * increments + lower));
        }

        // From: https://codereview.stackexchange.com/questions/122699/finding-a-cartesian-product-of-multiple-lists
        private static IEnumerable<IEnumerable<T>> CartesianProduct<T>(IEnumerable<IEnumerable<T>> sequences)
        {
            IEnumerable<IEnumerable<T>> emptyProduct = new[] { Enumerable.Empty<T>() };

            return sequences.Aggregate(
                emptyProduct,
                (accumulator, sequence) => accumulator.SelectMany(
                    accseq => sequence,
                    (accseq, item) => accseq.Concat(new[] { item })));
        }

        private static void WriteConvenientCsvs(SearchScorerSettings settings)
        {
            // Output data in more convenient formats.
            GitHubUsageCsvWriter.Write(
                settings.GitHubUsageCsvPath,
                GitHubUsageJsonReader.Read(settings.GitHubUsageJsonPath));
            TopSearchSelectionsV2CsvWriter.Write(
                settings.TopSearchSelectionsV2CsvPath,
                TopSearchSelectionsCsvReader.Read(settings.TopSearchSelectionsCsvPath));
        }

        private static async Task VerifyPackageIdsExistAsync(SearchScorerSettings settings, SearchClient searchClient)
        {
            var validator = new PackageIdPatternValidator(searchClient);

            // Verify all desired package IDs exist.
            var feedback = FeedbackSearchQueriesCsvReader
                .Read(settings.FeedbackSearchQueriesCsvPath)
                .SelectMany(x => x.MostRelevantPackageIds);

            var curated = CuratedSearchQueriesCsvReader
                .Read(settings.CuratedSearchQueriesCsvPath)
                .SelectMany(x => x.PackageIdToScore.Keys);

            Console.WriteLine("Searching for non-existent package IDs");
            var allPackageIds = feedback.Concat(curated);
            var nonExistentPackageIds = await validator.GetNonExistentPackageIdsAsync(allPackageIds, settings);
            Console.WriteLine();
            Console.WriteLine($"Found {nonExistentPackageIds.Count}.");
            foreach (var packageId in nonExistentPackageIds)
            {
                Console.WriteLine($" - {packageId}");
            }
        }
    }
}