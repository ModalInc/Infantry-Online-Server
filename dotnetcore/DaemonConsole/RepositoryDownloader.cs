﻿using Octokit;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DaemonConsole
{
    /// <summary>
    /// Allows for downloading of releases for zones and infrastructure files.
    /// </summary>
    /// <param name="configuration"></param>
    internal class RepositoryDownloader (BaseConfiguration configuration)
    {
        public async Task<ZipArchive> FetchLatestZoneServerRelease()
        {
            string assetName;

            if (OperatingSystem.IsWindows())
            {
                assetName = configuration
                    .Repository
                    .ZoneServerWindowsPackageName.ToLower();
            }
            else
            {
                assetName = configuration
                    .Repository
                    .ZoneServerLinuxPackageName.ToLower();
            }

            return await FetchLatestReleaseAsset(assetName);
        }

        private async Task<ZipArchive> FetchLatestReleaseAsset(string assetName)
        {
            var githubClient = new GitHubClient(new ProductHeaderValue("InfantryDaemon"));

            var osName = OperatingSystem.IsWindows() ? "Windows" : "Linux/Other";

            Console.WriteLine($"Getting latest release (OS Detected: {osName})...");

            var release = await githubClient
                .Repository
                .Release
                .GetLatest(configuration.Repository.GitHubOwnerName, configuration.Repository.GitHubRepositoryName);

            var asset = release.Assets.FirstOrDefault(a => a.Name.ToLower() == assetName);

            if (asset == null)
            {
                throw new Exception($"Error: Asset for downloading not found: {assetName}");
            }

            using var http = new HttpClient();

            Console.WriteLine($"Downloading latest {asset.Name} ({asset.Size} bytes)... ");

            var remoteStream = await http.GetStreamAsync(asset.BrowserDownloadUrl);
            var outputStream = new MemoryStream();

            Console.WriteLine($"Asset downloaded: {asset.Name}");

            await remoteStream.CopyToAsync(outputStream);
            outputStream.Position = 0;

            var archive = new ZipArchive(outputStream);

            return archive;
        }
    }
}
