﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Knapcode.NuGetProtocol.Shared;

namespace Knapcode.NuGetProtocol.V2
{
    public class Client
    {
        private static readonly ConcurrentDictionary<string, Task<Metadata>> _metadata
            = new ConcurrentDictionary<string, Task<Metadata>>();

        private readonly Protocol _protocol;
        private readonly PackageReader _packageReader;

        public Client(Protocol protocol, PackageReader packageReader)
        {
            _protocol = protocol;
            _packageReader = packageReader;
        }

        public async Task<Metadata> GetMetadataAsync(PackageSource source)
        {
            return await _metadata.GetOrAdd(
                source.SourceUri,
                _ => _protocol.GetMetadataAsync(source));
        }

        public async Task<HttpStatusCode> PushPackageAsync(PackageSource source, Stream package)
        {
            return await _protocol.PushPackageAsync(source, package);
        }

        public async Task<HttpStatusCode> DeletePackageAsync(PackageSource source, PackageIdentity package)
        {
            return await _protocol.DeletePackageAsync(source, package);
        }

        public async Task<HttpResult<PackageEntry>> GetPackageEntryAsync(PackageSource source, PackageIdentity package)
        {
            return await _protocol.GetPackageEntryAsync(source, package);
        }

        public async Task<HttpResult<PackageFeed>> GetPackageCollectionAsync(PackageSource source, string filter)
        {
            return await _protocol.GetPackageCollectionAsync(source, filter);
        }

        public async Task<HttpResult<PackageEntry>> GetPackageEntryFromCollectionWithCustomFilterAsync(PackageSource source, PackageIdentity package)
        {
            return await GetPackageEntryFromCollection(
                source,
                $"Id eq '{package.Id}' and Version eq '{package.Version}' and not startswith(Id, '!IMPOSSIBLE!')");
        }

        public async Task<HttpResult<PackageEntry>> GetPackageEntryFromCollectionWithSimpleFilterAsync(PackageSource source, PackageIdentity package)
        {
            return await GetPackageEntryFromCollection(
                source,
                $"Id eq '{package.Id}' and Version eq '{package.Version}'");
        }

        private async Task<HttpResult<PackageEntry>> GetPackageEntryFromCollection(PackageSource source, string filter)
        {
            var result = await _protocol.GetPackageCollectionAsync(source, filter);

            if (result.StatusCode != HttpStatusCode.OK)
            {
                return new HttpResult<PackageEntry>
                {
                    StatusCode = result.StatusCode,
                };
            }

            var count = result.Data.Entries.Count();
            if (count == 0)
            {
                return new HttpResult<PackageEntry>
                {
                    StatusCode = HttpStatusCode.NotFound,
                };
            }
            else if (count == 0)
            {
                throw new InvalidDataException($"Either zero or one results are expected. {count} were returned.");
            }

            return new HttpResult<PackageEntry>
            {
                StatusCode = HttpStatusCode.OK,
                Data = result.Data.Entries.First(),
            };
        }

        public async Task<ConditionalPushResult> PushPackageIfNotExistsAsync(PackageSource source, Stream package)
        {
            var identity = _packageReader.GetPackageIdentity(package);

            package.Position = 0;

            var packageResultBeforePush = await GetPackageEntryAsync(source, identity);
            if (packageResultBeforePush.StatusCode == HttpStatusCode.OK)
            {
                return new ConditionalPushResult
                {
                    PackageAlreadyExists = true,
                    PackageResult = packageResultBeforePush,
                };
            }

            var stopwatch = Stopwatch.StartNew();
            var pushStatusCode = await PushPackageAsync(source, package);
            var timeToPush = stopwatch.Elapsed;
            var packageStatusCode = HttpStatusCode.NotFound;
            HttpResult<PackageEntry> packageResultAfterPush = null;
            while (packageStatusCode == HttpStatusCode.NotFound &&
                   stopwatch.Elapsed < TimeSpan.FromMinutes(20))
            {
                packageResultAfterPush = await GetPackageEntryAsync(source, identity);
                packageStatusCode = packageResultAfterPush.StatusCode;
                if (packageStatusCode == HttpStatusCode.NotFound)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            return new ConditionalPushResult
            {
                PackageAlreadyExists = false,
                PackageResult = packageResultAfterPush,
                PackagePushSuccessfully = packageStatusCode == HttpStatusCode.OK,
                PushStatusCode = pushStatusCode,
                TimeToPush = timeToPush,
                TimeToBeAvailable = packageStatusCode == HttpStatusCode.OK ? stopwatch.Elapsed : (TimeSpan?)null,
            };
        }

        public async Task<ConditionalPushResult> PushAndUnlistPackageIfNotExistsAsync(PackageSource source, Stream package)
        {
            var identity = _packageReader.GetPackageIdentity(package);
            package.Position = 0;
            var result = await PushPackageIfNotExistsAsync(source, package);
            var deleteStatusCode = await _protocol.DeletePackageAsync(source, identity);

            return result;
        }
    }
}
