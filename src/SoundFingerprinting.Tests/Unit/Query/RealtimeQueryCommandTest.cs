namespace SoundFingerprinting.Tests.Unit.Query
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging.Abstractions;
    using Moq;
    using NUnit.Framework;
    using SoundFingerprinting.Audio;
    using SoundFingerprinting.Builder;
    using SoundFingerprinting.Command;
    using SoundFingerprinting.Configuration;
    using SoundFingerprinting.DAO.Data;
    using SoundFingerprinting.Data;
    using SoundFingerprinting.InMemory;
    using SoundFingerprinting.Query;
    using SoundFingerprinting.Strides;

    [TestFixture]
    public class RealtimeQueryCommandTest
    {
        private readonly int sampleRate = 5512;
        private readonly int minSamplesPerFingerprint;
        private readonly double minSizeChunkDuration;
        
        public RealtimeQueryCommandTest()
        {
            var config = new DefaultFingerprintConfiguration();
            minSamplesPerFingerprint = config.SpectrogramConfig.MinimumSamplesPerFingerprint;
            minSizeChunkDuration = (double)minSamplesPerFingerprint / sampleRate;
        }
        
        [Test]
        public async Task RealtimeQueryShouldMatchOnlySelectedClusters()
        {
            var modelService = new InMemoryModelService();
            int count = 10, foundWithClusters = 0, foundWithWrongClusters = 0, testWaitTime = 3000;
            var data = GenerateRandomAudioChunks(count, 1, DateTime.UtcNow);
            var concatenated = Concatenate(data);
            var hashes = await FingerprintCommandBuilder.Instance
                                                .BuildFingerprintCommand()
                                                .From(concatenated)
                                                .Hash();

            modelService.Insert(new TrackInfo("312", "Bohemian Rhapsody", "Queen", new Dictionary<string, string>{{ "country", "USA" }}), hashes);
            
            var cancellationTokenSource = new CancellationTokenSource(testWaitTime);
            var wrong = QueryCommandBuilder.Instance.BuildRealtimeQueryCommand()
                                              .From(SimulateRealtimeQueryData(data, jitterLength: 0))
                                              .WithRealtimeQueryConfig(config =>
                                              {
                                                    config.ResultEntryFilter = new TrackMatchLengthEntryFilter(15d);
                                                    config.SuccessCallback = _ => Interlocked.Increment(ref foundWithWrongClusters);
                                                    config.YesMetaFieldsFilter = new Dictionary<string, string> {{"country", "CANADA"}};
                                                    return config;
                                              })
                                              .UsingServices(modelService)
                                              .Query(cancellationTokenSource.Token);
            
            var right = QueryCommandBuilder.Instance
                                .BuildRealtimeQueryCommand()
                                .From(SimulateRealtimeQueryData(data, jitterLength: 0))
                                .WithRealtimeQueryConfig(config =>
                                {
                                    config.ResultEntryFilter = new TrackMatchLengthEntryFilter(15d);
                                    config.SuccessCallback = _ => Interlocked.Increment(ref foundWithClusters);
                                    config.YesMetaFieldsFilter = new Dictionary<string, string> {{"country", "USA"}};
                                    return config;
                                })
                                .UsingServices(modelService)
                                .Query(cancellationTokenSource.Token);

            await Task.WhenAll(wrong, right);

            Assert.AreEqual(1, foundWithClusters);
            Assert.AreEqual(0, foundWithWrongClusters);
        }
        
        [Test]
        public async Task RealtimeQueryStrideShouldBeUsed()
        {
            var modelService = new InMemoryModelService();
            int staticStride = 1024;
            double permittedGap = (double) minSamplesPerFingerprint / sampleRate;
            int count = 10, found = 0, didNotPassThreshold = 0, fingerprintsCount = 0;
            int testWaitTime = 3000;
            var data = GenerateRandomAudioChunks(count, 1, DateTime.UtcNow);
            var concatenated = Concatenate(data);
            var hashes = await FingerprintCommandBuilder.Instance
                                                .BuildFingerprintCommand()
                                                .From(concatenated)
                                                .Hash();

            modelService.Insert(new TrackInfo("312", "Bohemian Rhapsody", "Queen"), hashes);
            
            var collection = SimulateRealtimeQueryData(data, jitterLength: 0);
            var cancellationTokenSource = new CancellationTokenSource(testWaitTime);
            
            double duration = await QueryCommandBuilder.Instance.BuildRealtimeQueryCommand()
                                              .From(collection)
                                              .WithRealtimeQueryConfig(config =>
                                              {
                                                    config.QueryConfiguration.Audio.Stride = new IncrementalStaticStride(staticStride);
                                                    config.SuccessCallback = _ => Interlocked.Increment(ref found);
                                                    config.DidNotPassFilterCallback = _ => Interlocked.Increment(ref didNotPassThreshold);
                                                    config.QueryConfiguration.Audio.PermittedGap = permittedGap;
                                                    return config;
                                              })
                                              .Intercept(fingerprints =>
                                              {
                                                  Interlocked.Add(ref fingerprintsCount, fingerprints.Audio?.Count ?? 0 + fingerprints.Video?.Count ?? 0);
                                                  return fingerprints;
                                              })
                                              .UsingServices(modelService)
                                              .Query(cancellationTokenSource.Token);

            Assert.AreEqual((count - 1) * minSamplesPerFingerprint / staticStride + 1, fingerprintsCount);
            Assert.AreEqual((double)count * minSamplesPerFingerprint / 5512, duration, 0.00001);
        }
        
        [Test]
        public async Task ShouldQueryInRealtime()
        {
            var modelService = new InMemoryModelService();

            double minSizeChunk = (double)minSamplesPerFingerprint / sampleRate; // length in seconds of one query chunk ~1.8577
            const double totalTrackLength = 210;       // length of the track 3 minutes 30 seconds.
            int count = (int)Math.Round(totalTrackLength / minSizeChunk), fingerprintsCount = 0, queryMatchLength = 10, ongoingCalls = 0;
            var data = GenerateRandomAudioChunks(count, seed: 1, DateTime.UtcNow);
            var concatenated = Concatenate(data);
            var avHashes = await FingerprintCommandBuilder.Instance
                                                .BuildFingerprintCommand()
                                                .From(concatenated)
                                                .Hash();

            Assert.NotNull(avHashes.Audio);
            
            // hashes have to be equal to total track length +- 1 second
            Assert.AreEqual(totalTrackLength, avHashes.Audio.DurationInSeconds, delta: 1);
            
            // store track data and associated hashes
            modelService.Insert(new TrackInfo("312", "Bohemian Rhapsody", "Queen"), avHashes);

            var successMatches = new List<ResultEntry>();
            var didNotGetToContiguousQueryMatchLengthMatch = new List<ResultEntry>();

            var defaultAvQueryConfiguration = new DefaultAVQueryConfiguration
            {
                Audio =
                {
                    Stride = new IncrementalRandomStride(256, 512),
                    PermittedGap = 2
                }
            };
            
            var realtimeConfig = new RealtimeQueryConfiguration(defaultAvQueryConfiguration, new TrackMatchLengthEntryFilter(queryMatchLength), 
                successCallback: result =>
                {
                    foreach (var (entry, _) in result.ResultEntries)
                    {
                        Console.WriteLine($"Found Match Starts At {entry.TrackMatchStartsAt:0.000}, Match Length {entry.TrackCoverageWithPermittedGapsLength:0.000}, Query Length {entry.QueryLength:0.000} Track Starts At {entry.TrackStartsAt:0.000}");
                        successMatches.Add(entry);
                    }
                },
                didNotPassFilterCallback: result =>
                {
                    foreach (var (entry, _) in result.ResultEntries)
                    {
                        Console.WriteLine($"Entry didn't pass filter, Starts At {entry.TrackMatchStartsAt:0.000}, Match Length {entry.TrackCoverageWithPermittedGapsLength:0.000}, Query Length {entry.TrackCoverageWithPermittedGapsLength:0.000}");
                        didNotGetToContiguousQueryMatchLengthMatch.Add(entry);
                    }
                },
                new OngoingRealtimeResultEntryFilter(minCoverage: 0.2d, minTrackLength: 1d),
                ongoingSuccessCallback: _ => { Interlocked.Increment(ref ongoingCalls); },
                errorCallback: (error, _) => throw error,
                restoredAfterErrorCallback: () => throw new Exception("Downtime callback called"),
                offlineStorage: new EmptyOfflineStorage(), 
                errorBackoffPolicy: new RandomExponentialBackoffPolicy(),
                delayStrategy: new RandomDelayStrategy(1, 5));

            // simulating realtime query, starting in the middle of the track ~1 min 45 seconds (105 seconds).
            // and we query for 35 seconds
            const double queryLength = 35;
            // track  ---------------------------- 210 seconds
            // query                ----           35 seconds
            // match starts at      |              105 second
            var realtimeQuery = data.Skip(count / 2).Take((int)(queryLength/minSizeChunk) + 1).ToArray();

            double duration = realtimeQuery.Sum(_ => _.Duration);
            Assert.AreEqual(queryLength, duration, 1); // asserting the total length of the query +- 1 second
            
            // adding some jitter before and after the query which should not match
            // track           ---------------------------- 210 seconds
            // q with jitter             ^^^----^^^         10 sec + 35 seconds + 10 sec = 55 sec
            // match starts at              |               105 second
            const double jitterLength = 10;
            var collection = SimulateRealtimeQueryData(realtimeQuery, jitterLength);
            double processed = await QueryCommandBuilder.Instance
                                            .BuildRealtimeQueryCommand()
                                            .From(collection)
                                            .WithRealtimeQueryConfig(realtimeConfig)
                                            .Intercept(fingerprints =>
                                            {
                                                Interlocked.Add(ref fingerprintsCount, fingerprints.Audio?.Count + fingerprints.Video?.Count ?? 0);
                                                return fingerprints;
                                            })
                                            .UsingServices(modelService)
                                            .Query(CancellationToken.None);

            // since we start from the middle of the track ~1 min 45 seconds and query for 35 seconds with 10 seconds filter
            // this means we will get 3 successful matches 
            // start: 105 seconds,  query length: 35 seconds, query match filter length: 10
            int matchesCount = (int)Math.Floor(queryLength / queryMatchLength);
            Assert.AreEqual(matchesCount, successMatches.Count);
            
            // since our realtime query was 35 seconds with 3 successful matches of 10
            // there has to be one more purged match of 5 seconds which did not get through successful filter
            Assert.AreEqual(1, didNotGetToContiguousQueryMatchLengthMatch.Count);
            
            // verifying that we queried the correct amount of seconds
            Assert.AreEqual(queryLength + 2 * jitterLength, processed, 1);

            // track starts to match at the middle
            // matches                      |||
            // q with jitter             ^^^---^^^        
            // expecting 3 matches at 105th, 115th and 125th second 
            double[] trackMatches = Enumerable.Repeat(totalTrackLength / 2, matchesCount).Select((matchAt, index) => matchAt + queryMatchLength * index).ToArray();
            for (int i = 0; i < trackMatches.Length; ++i)
            {
                Assert.AreEqual(trackMatches[i], successMatches[i].TrackMatchStartsAt, 2.5);
            }
            
            // ongoing calls have to be called every time when realtime chunk is sent since ongoing query match filter is equal to min-size chunk
            Assert.AreEqual(realtimeQuery.Length + 1 /*jitter call since track can continue in the next query*/, ongoingCalls);
        }

        [Test]
        public async Task ShouldNotLoseAudioSamplesInCaseIfExceptionIsThrown()
        {
            var modelService = new InMemoryModelService();

            double minSizeChunk = (double)minSamplesPerFingerprint / sampleRate; // length in seconds of one query chunk ~1.8577
            const double totalTrackLength = 210;                                 // length of the track 3 minutes 30 seconds.
            const double jitterLength = 10;
            double totalQueryLength = totalTrackLength + 2 * jitterLength;
            
            int trackCount = (int)(totalTrackLength / minSizeChunk), fingerprintsCount = 0, errored = 0, didNotPassThreshold = 0, jitterChunks = 2;
            
            var start = new DateTime(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            var data = GenerateRandomAudioChunks(trackCount, seed: 1, start);
            var concatenated = Concatenate(data);
            var hashes = await FingerprintCommandBuilder.Instance
                                                .BuildFingerprintCommand()
                                                .From(concatenated)
                                                .Hash();

            modelService.Insert(new TrackInfo("312", "Bohemian Rhapsody", "Queen"), hashes);

            var resultEntries = new List<AVResultEntry>();
            var collection = SimulateRealtimeQueryData(data, jitterLength);
            var offlineStorage = new OfflineStorage(Path.GetTempPath());
            var restoreCalled = new bool[1];
            double processed = await new RealtimeQueryCommand(FingerprintCommandBuilder.Instance, new FaultyQueryService(faultyCounts: trackCount + jitterChunks - 1, QueryFingerprintService.Instance), new NullLoggerFactory())
                 .From(collection)
                 .WithRealtimeQueryConfig(config =>
                 {
                     config.SuccessCallback = entry =>
                     {
                         resultEntries.AddRange(entry.ResultEntries);
                     };

                     config.DidNotPassFilterCallback = _ =>
                     {
                         Interlocked.Increment(ref didNotPassThreshold);
                     };
                     config.ErrorCallback = (_, _) =>
                     {
                         Interlocked.Increment(ref errored);
                     };

                     config.ResultEntryFilter = new TrackRelativeCoverageLengthEntryFilter(0.4, waitTillCompletion: true);
                     config.RestoredAfterErrorCallback = () => restoreCalled[0] = true;
                     config.OfflineStorage = offlineStorage;                            // store the other half of the fingerprints in the downtime hashes storage
                     config.DelayStrategy = new NoDelayStrategy();
                     return config;
                 })
                 .Intercept(fingerprints =>
                 {
                     Interlocked.Increment(ref fingerprintsCount);
                     return fingerprints;
                 })
                 .UsingServices(modelService)
                 .Query(CancellationToken.None);

            Assert.AreEqual(totalQueryLength, processed, minSizeChunkDuration);
            Assert.AreEqual(trackCount + jitterChunks - 1, errored);
            Assert.AreEqual(trackCount, fingerprintsCount - jitterChunks);
            Assert.IsTrue(restoreCalled[0]);
            Assert.AreEqual(0, didNotPassThreshold);
            Assert.AreEqual(1, resultEntries.Count);
            var (result, _) = resultEntries.First();
            Assert.AreEqual(totalTrackLength, result.Coverage.TrackCoverageWithPermittedGapsLength, 2 * minSizeChunkDuration);
            Assert.IsTrue(Math.Abs(start.Subtract(result.MatchedAt).TotalSeconds) < 2, $"Matched At {result.MatchedAt:o}");
        }

        [Test]
        public async Task HashesShouldMatchExactlyWhenAggregated()
        {
            var modelService = new InMemoryModelService();

            int count = 20;
            var data = GenerateRandomAudioChunks(count, seed: 1, DateTime.UtcNow);
            var concatenated = Concatenate(data);
            var (hashes, _) = await FingerprintCommandBuilder.Instance
                .BuildFingerprintCommand()
                .From(concatenated)
                .WithFingerprintConfig(config =>
                {
                    config.Audio.Stride = new IncrementalStaticStride(512);
                    return config;
                })
                .Hash();
            
            var collection = SimulateRealtimeQueryData(data, jitterLength: 0);
            var list = new List<AVHashes>();
            
            await QueryCommandBuilder.Instance.BuildRealtimeQueryCommand()
                .From(collection)
                .WithRealtimeQueryConfig(config =>
                {
                    config.QueryConfiguration.Audio.Stride = new IncrementalStaticStride(512);
                    return config;
                })
                .Intercept(timedHashes =>
                {
                    list.Add(timedHashes);
                    return timedHashes;
                })
                .UsingServices(modelService)
                .Query(CancellationToken.None);
            
            Assert.AreEqual(hashes.Count, list.Select(entry => entry.Audio?.Count).Sum());
            var merged = Hashes.Aggregate(list.Select(_ => _.Audio), concatenated.Duration).ToList();
            Assert.AreEqual(1, merged.Count, $"Hashes:{string.Join(",", merged.Select(_ => $"{_.RelativeTo},{_.DurationInSeconds:0.00}"))}");
            Assert.AreEqual(hashes.Count, merged.Select(entry => entry.Count).Sum());

            var aggregated = Hashes.Aggregate(list.Select(_ => _.Audio), double.MaxValue).ToList();
            Assert.AreEqual(1, aggregated.Count);
            Assert.AreEqual(hashes.Count, aggregated[0].Count);
            foreach (var zipped in hashes.OrderBy(h => h.SequenceNumber).Zip(aggregated[0], (a, b) => new { a, b }))
            {
                Assert.AreEqual(zipped.a.StartsAt, zipped.b.StartsAt, 1d);
                Assert.AreEqual(zipped.a.SequenceNumber, zipped.b.SequenceNumber);
                CollectionAssert.AreEqual(zipped.a.HashBins, zipped.b.HashBins);
            }
        }

        [Test]
        public async Task QueryingWithAggregatedHashesShouldResultInTheSameMatches()
        {
            var modelService = new InMemoryModelService();

            int count = 20, testWaitTime = 5000;
            var data = GenerateRandomAudioChunks(count, 1, DateTime.UtcNow);
            var concatenated = Concatenate(data);
            var hashes = await FingerprintCommandBuilder.Instance
                .BuildFingerprintCommand()
                .From(concatenated)
                .WithFingerprintConfig(config => config)
                .Hash();

            modelService.Insert(new TrackInfo("312", "Bohemian Rhapsody", "Queen"), hashes);

            var collection = SimulateRealtimeQueryData(data, jitterLength: 0);
            var cancellationTokenSource = new CancellationTokenSource(testWaitTime);
            var fingerprints = new List<AVHashes>();
            var entries = new List<AVResultEntry>();
            
            await QueryCommandBuilder.Instance.BuildRealtimeQueryCommand()
                .From(collection)
                .WithRealtimeQueryConfig(config =>
                {
                    config.SuccessCallback = entry => entries.AddRange(entry.ResultEntries);
                    config.ResultEntryFilter = new TrackRelativeCoverageLengthEntryFilter(0.8d);
                    config.QueryConfiguration.Audio.Stride = new IncrementalStaticStride(2048);
                    return config;
                })
                .Intercept(queryHashes =>
                {
                    fingerprints.Add(queryHashes);
                    return queryHashes;
                })
                .UsingServices(modelService)
                .Query(cancellationTokenSource.Token);

            Assert.IsTrue(entries.Any());
            Assert.AreEqual(1, entries.Count);
            var (realtimeResult, _) = entries.First();
            var aggregatedHashes = Hashes.Aggregate(fingerprints.Select(_ => _.Audio), 60d).First();
            var (nonRealtimeResult, _) = await QueryCommandBuilder.Instance
                .BuildQueryCommand()
                .From(new AVHashes(aggregatedHashes, null))
                .UsingServices(modelService)
                .Query();
            
            Assert.IsTrue(nonRealtimeResult.ContainsMatches);
            Assert.AreEqual(1, nonRealtimeResult.ResultEntries.Count());
            Assert.AreEqual(realtimeResult.MatchedAt, aggregatedHashes.RelativeTo);
            Assert.AreEqual(realtimeResult.MatchedAt, nonRealtimeResult.BestMatch?.MatchedAt, $"Realtime vs NonRealtime {nonRealtimeResult.BestMatch?.Coverage.BestPath.Count()} match time does not match");
        }

        [Test]
        public async Task ShouldPurgeCompletedMatchWhenAsyncCollectionIsExhausted()
        {
            var modelService = new InMemoryModelService();

            const double totalTrackLength = 210;       // length of the track 3 minutes 30 seconds.

            var data = GenerateRandomAudioChunks((int)(totalTrackLength / minSizeChunkDuration), seed: 1, DateTime.UtcNow);
            var concatenated = Concatenate(data);
            var hashes = await FingerprintCommandBuilder.Instance
                .BuildFingerprintCommand()
                .From(concatenated)
                .Hash();

            modelService.Insert(new TrackInfo("312", "Bohemian Rhapsody", "Queen"), hashes);

            var success = new List<AVResultEntry>();
            var didNotPass = new List<AVResultEntry>();
            await QueryCommandBuilder.Instance.BuildRealtimeQueryCommand()
                .From(SimulateRealtimeQueryData(data, jitterLength: 0))
                .WithRealtimeQueryConfig(config =>
                {
                    config.ResultEntryFilter = new TrackRelativeCoverageLengthEntryFilter(0.5, true);
                    config.SuccessCallback = _ => success.AddRange(_.ResultEntries);
                    config.DidNotPassFilterCallback = _ => didNotPass.AddRange(_.ResultEntries);
                    return config;
                })
                .UsingServices(modelService)
                .Query(CancellationToken.None);

            Assert.AreEqual(0, didNotPass.Count);
            Assert.AreEqual(1, success.Count);
        }

        [Test]
        public async Task ShouldContinueQueryingEvenWhenAnErrorOccurs()
        {
            const int length = 60;
            const int throwExceptionAfter = 3;
            const int totalExceptions = 5;
            int exceptions = 0, restored = 0;
            var cancellationTokenSource = new CancellationTokenSource();
            var indefiniteSource = GetSamplesIndefinitely(length, throwExceptionAfter);
            var backoffPolicy = new Mock<IBackoffPolicy>(MockBehavior.Strict);
            backoffPolicy.Setup(p => p.RemainingDelay).Returns(TimeSpan.Zero);
            backoffPolicy.Setup(p => p.Failure());
            backoffPolicy.Setup(p => p.Success());
            
            var modelService = new Mock<IModelService>(MockBehavior.Strict);
            modelService.Setup(s => s.Query(It.IsAny<Hashes>(), It.IsAny<QueryConfiguration>())).Returns(Enumerable.Empty<SubFingerprintData>());

            var queryLength = await QueryCommandBuilder.Instance
                .BuildRealtimeQueryCommand()
                .From(indefiniteSource)
                .WithRealtimeQueryConfig(config =>
                {
                    config.ErrorCallback = (_, _) =>
                    {
                        exceptions++;
                        if (exceptions == totalExceptions)
                        {
                            cancellationTokenSource.Cancel();
                        }
                    };

                    config.RestoredAfterErrorCallback = () => restored++;
                    config.ErrorBackoffPolicy = backoffPolicy.Object;
                    return config;
                })
                .UsingServices(modelService.Object)
                .Query(cancellationTokenSource.Token);

            int totalQueries = throwExceptionAfter * totalExceptions - totalExceptions;
            
            Assert.AreEqual(totalExceptions, exceptions);
            Assert.AreEqual(totalExceptions - 1, restored);
            Assert.AreEqual(totalQueries * length, queryLength);
            
            modelService.Verify(s => s.Query(It.IsAny<Hashes>(), It.IsAny<QueryConfiguration>()), Times.Exactly(totalQueries));
            backoffPolicy.Verify(b => b.Failure(), Times.Exactly(totalExceptions));
            backoffPolicy.Verify(b => b.RemainingDelay, Times.Exactly(totalExceptions));
            backoffPolicy.Verify(b => b.Success(), Times.Exactly(totalExceptions - 1));
        }

        private static async IAsyncEnumerable<AudioSamples> GetSamplesIndefinitely(int eachLengthSeconds, int throwExceptionAfter)
        {
            int count = 0;
            while (true)
            {
                count++;
                if (count % throwExceptionAfter == 0)
                {
                    throw new Exception("Error while reading samples from the source.");
                }

                await Task.Delay(TimeSpan.Zero);
                yield return TestUtilities.GenerateRandomAudioSamples(eachLengthSeconds * 5512);
            }
        }

        private static AudioSamples Concatenate(IReadOnlyList<AudioSamples> data)
        {
            int length = data.Sum(samples => samples.Samples.Length);
            float[] concatenated = new float[length];
            int dest = 0;
            foreach (var audioSamples in data)
            {
                Array.Copy(audioSamples.Samples, 0, concatenated, dest, audioSamples.Samples.Length);
                dest += audioSamples.Samples.Length;
            }

            var first = data.First();
            return new AudioSamples(concatenated, first.Origin, first.SampleRate, first.RelativeTo);
        }

        private List<AudioSamples> GenerateRandomAudioChunks(int count, int seed, DateTime relativeTo)
        {
            return Enumerable
                .Range(0, count)
                .Select(index => GetMinSizeOfAudioSamples(seed * index, relativeTo.AddSeconds(index * minSizeChunkDuration)))
                .ToList();
        }

        /// <summary>
        ///  Simulate realtime query data.
        /// </summary>
        /// <param name="audioSamples">Chunks of audio samples.</param>
        /// <param name="jitterLength">Jitter length in seconds, added at the beginning and at the end.</param>
        /// <returns>Async enumerable.</returns>
        private static IAsyncEnumerable<AudioSamples> SimulateRealtimeQueryData(IReadOnlyCollection<AudioSamples> audioSamples, double jitterLength)
        {
            var collection = new BlockingCollection<AudioSamples>();
            Task.Factory.StartNew(() =>
            {
                if (jitterLength > 0)
                {
                    var startAt = audioSamples.First()?.RelativeTo.AddSeconds(-jitterLength) ?? DateTime.UtcNow;
                    Jitter(collection, jitterLength, startAt);
                }

                foreach (var audioSample in audioSamples)
                {
                    collection.Add(new AudioSamples(audioSample.Samples, audioSample.Origin, audioSample.SampleRate, audioSample.RelativeTo));
                }

                if (jitterLength > 0)
                {
                    var endsAt = audioSamples.Last()?.RelativeTo ?? DateTime.UtcNow;
                    Jitter(collection, jitterLength, endsAt);
                }

                collection.CompleteAdding();
            });

            return new BlockingRealtimeCollection<AudioSamples>(collection);
        }

        private static void Jitter(BlockingCollection<AudioSamples> collection, double jitterLength, DateTime dateTime)
        {
            double sum = 0d;
            do
            {
                var audioSample = TestUtilities.GenerateRandomAudioSamples((int)(jitterLength * 5512));
                collection.Add(new AudioSamples(audioSample.Samples, audioSample.Origin, audioSample.SampleRate, dateTime));
                sum += audioSample.Duration;
            } 
            while (sum < jitterLength);
        }

        private AudioSamples GetMinSizeOfAudioSamples(int seed, DateTime relativeTo)
        {
            var samples = TestUtilities.GenerateRandomFloatArray(minSamplesPerFingerprint, seed);
            return new AudioSamples(samples, "cnn", 5512, relativeTo);
        }

        private class FaultyQueryService : IQueryFingerprintService
        {
            private readonly IQueryFingerprintService goodOne;

            private int faultyCounts;

            public FaultyQueryService(int faultyCounts, IQueryFingerprintService goodOne)
            {
                this.faultyCounts = faultyCounts;
                this.goodOne = goodOne;
            }
            
            public QueryResult Query(Hashes queryFingerprints, QueryConfiguration configuration, IModelService modelService)
            {
                if (faultyCounts-- > 0)
                {
                    throw new IOException("I/O exception");
                }

                return goodOne.Query(queryFingerprints, configuration, modelService);
            }
        }
    }
}