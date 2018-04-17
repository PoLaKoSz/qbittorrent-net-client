﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BencodeNET.Parsing;
using BencodeNET.Torrents;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Xunit;

namespace QBittorrent.Client.Tests
{
    [Collection(DockerCollection.Name)]
    public class QBittorrentClientTests : IAsyncLifetime, IDisposable
    {
        private string ContainerId { get; set; }

        private DockerFixture DockerFixture { get; }

        private QBittorrentClient Client { get; }

        #region Lifetime

        public QBittorrentClientTests(DockerFixture dockerFixture)
        {
            DockerFixture = dockerFixture;
            Client = new QBittorrentClient(new Uri("http://localhost:8080"));
        }

        public void Dispose()
        {
            Client?.Dispose();
        }

        public async Task InitializeAsync()
        {
            var createContainerParameters = new CreateContainerParameters
            {
                Image = DockerFixture.ImageName,
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    ["8080/tcp"] = new EmptyStruct()
                },
            };

            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                createContainerParameters.HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["8080/tcp"] = new List<PortBinding>
                        {
                            new PortBinding
                            {
                                HostIP = "0.0.0.0",
                                HostPort = "8080"
                            }
                        }
                    }
                };
            }

            Console.WriteLine("\tCreating container from image...");
            var result = await DockerFixture.Client.Containers.CreateContainerAsync(
                createContainerParameters);
            ContainerId = result.ID;
            Assert.False(string.IsNullOrEmpty(ContainerId), "string.IsNullOrEmpty(ContainerId)");
            Console.WriteLine($"\tCreated container {ContainerId}.");
            
            Console.WriteLine($"\tStarting container {ContainerId}...");
            var started = await DockerFixture.Client.Containers.StartContainerAsync(ContainerId,
                new ContainerStartParameters());
            Assert.True(started, "started");
            Console.WriteLine($"\tStarted container {ContainerId}.");
        }

        public async Task DisposeAsync()
        {
            Console.WriteLine($"\tStopping container {ContainerId}...");
            await DockerFixture.Client.Containers.StopContainerAsync(ContainerId,
                new ContainerStopParameters {WaitBeforeKillSeconds = 10u});
            Console.WriteLine($"\tDeleting container {ContainerId}...");
            await DockerFixture.Client.Containers.RemoveContainerAsync(ContainerId,
                new ContainerRemoveParameters {Force = true});
        }

        #endregion

        #region Login/Logout
        
        [Fact]
        public async Task LoginCorrect()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var list = await Client.GetTorrentListAsync();
            list.Should().BeEmpty();
        }
        
        [Fact]
        public async Task LoginIncorrect()
        {
            await Client.LoginAsync("admin", "incorrect");
            await Assert.ThrowsAsync<HttpRequestException>(() => Client.GetTorrentListAsync());
        }
        
        [Fact]
        public async Task NoLogin()
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Client.GetTorrentListAsync());
        }

        [Fact]
        public async Task Logout()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var list = await Client.GetTorrentListAsync();
            list.Should().BeEmpty();
            await Client.LogoutAsync();
            await Assert.ThrowsAsync<HttpRequestException>(() => Client.GetTorrentListAsync());
        }
        
        #endregion
        
        #region Add Torrent

        [Fact]
        public async Task AddTorrentsFromFiles()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var list = await Client.GetTorrentListAsync();
            list.Should().BeEmpty();

            var parser = new BencodeParser();
            var filesToAdd = Directory.GetFiles(Utils.TorrentsFolder, "*.torrent");
            var torrents = filesToAdd
                .Select(path => parser.Parse<Torrent>(path))
                .ToList();
            var hashes = torrents.Select(t => t.OriginalInfoHash.ToLower());
            
            var addRequest = new AddTorrentFilesRequest(filesToAdd) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);

            await Task.Delay(1000);
            
            list = await Client.GetTorrentListAsync();
            list.Should().HaveCount(filesToAdd.Length);
            list.Select(t => t.Hash).Should().BeEquivalentTo(hashes);
        }
        
        [Fact]
        public async Task AddTorrentsFromMagnetLinks()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var list = await Client.GetTorrentListAsync();
            list.Should().BeEmpty();

            var parser = new BencodeParser();
            var filesToAdd = Directory.GetFiles(Utils.TorrentsFolder, "*.torrent");
            var torrents = filesToAdd
                .Select(path => parser.Parse<Torrent>(path))
                .ToList();
            var magnets = torrents.Select(t => new Uri(t.GetMagnetLink()));
     
            var addRequest = new AddTorrentUrlsRequest(magnets) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);

            await Task.Delay(1000);

            list = await Client.GetTorrentListAsync();
            list.Should().HaveCount(filesToAdd.Length);
        }
        
        [Fact]
        public async Task AddTorrentsFromHttpLinks()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var list = await Client.GetTorrentListAsync();
            list.Should().BeEmpty();

            var links = new Uri[]
            {
                new Uri(
                    "http://releases.ubuntu.com/17.10/ubuntu-17.10.1-desktop-amd64.iso.torrent?_ga=2.234486046.1639235846.1523865053-1922367372.1523865053"),
                new Uri(
                    "http://releases.ubuntu.com/16.04/ubuntu-16.04.4-desktop-amd64.iso.torrent?_ga=2.234486046.1639235846.1523865053-1922367372.1523865053"),
            };
            var addRequest = new AddTorrentUrlsRequest(links) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);

            await Task.Delay(1000);

            await Utils.Retry(async () =>
            {
                list = await Client.GetTorrentListAsync();
                list.Should().HaveCount(2);            
                list.Should().Contain(t => t.Hash == "f07e0b0584745b7bcb35e98097488d34e68623d0");
                list.Should().Contain(t => t.Hash == "778ce280b595e57780ff083f2eb6f897dfa4a4ee");
            });
        }

        #endregion

        #region GetTorrentPropertiesAsync
        
        [Fact]
        public async Task GetTorrentProperties()
        {
            await Client.LoginAsync("admin", "adminadmin");

            var torrentPath = Path.Combine(Utils.TorrentsFolder, "ubuntu-16.04.4-desktop-amd64.iso.torrent");
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>(torrentPath);
            
            var addRequest = new AddTorrentFilesRequest(torrentPath) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);
            
            await Task.Delay(1000);

            var props = await Client.GetTorrentPropertiesAsync(torrent.OriginalInfoHash.ToLower());
            props.Should().NotBeNull();
            props.AdditionDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
            props.Comment.Should().Be(torrent.Comment);
            props.CreatedBy.Should().Be(torrent.CreatedBy ?? string.Empty);
            props.CreationDate.Should().Be(torrent.CreationDate);
            props.PieceSize.Should().Be(torrent.PieceSize);
            props.Size.Should().Be(torrent.TotalSize);
        }

        [Fact]
        public async Task GetTorrentPropertiesUnknown()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var props = await Client.GetTorrentPropertiesAsync("0000000000000000000000000000000000000000");
            props.Should().BeNull(because: "torrent with this hash has not been added");
        }

        #endregion

        #region GetTorrentContentsAsync

        [Fact]
        public async Task GetTorrentContentsSingle()
        {
            await Client.LoginAsync("admin", "adminadmin");

            var torrentPath = Path.Combine(Utils.TorrentsFolder, "ubuntu-16.04.4-desktop-amd64.iso.torrent");
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>(torrentPath);
            
            var addRequest = new AddTorrentFilesRequest(torrentPath) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);
            
            await Task.Delay(1000);

            var contents = await Client.GetTorrentContentsAsync(torrent.OriginalInfoHash.ToLower());
            contents.Should().NotBeNull();
            contents.Should().HaveCount(1);

            var content = contents.Single();
            content.Name.Should().Be(torrent.File.FileName);
            content.Size.Should().Be(torrent.File.FileSize);          
        }
        
        [Fact]
        public async Task GetTorrentContentsMulti()
        {
            await Client.LoginAsync("admin", "adminadmin");

            var torrentPath = Path.Combine(Utils.TorrentsFolder, "ubuntu-14.04-pack.torrent");
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>(torrentPath);
            
            var addRequest = new AddTorrentFilesRequest(torrentPath) { CreateRootFolder = false, Paused = true };
            await Client.AddTorrentsAsync(addRequest);
            
            await Task.Delay(1000);

            var contents = await Client.GetTorrentContentsAsync(torrent.OriginalInfoHash.ToLower());
            contents.Should().NotBeNull();
            contents.Should().HaveCount(torrent.Files.Count);
            
            var pairs =
                (from content in contents
                    join file in torrent.Files on content.Name equals file.FullPath
                    select (content, file))
                .ToList();

            pairs.Should().HaveCount(torrent.Files.Count);
            foreach (var (content, file) in pairs)
            {
                content.Size.Should().Be(file.FileSize);
            }
        }

        [Fact]
        public async Task GetTorrentContentsUnknown()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var contents = await Client.GetTorrentContentsAsync("0000000000000000000000000000000000000000");
            contents.Should().BeNull(because: "torrent with this hash has not been added");
        }
        
        #endregion
        
        #region GetTorrentTrackersAsync
        
        [Fact]
        public async Task GetTorrentTrackers()
        {
            await Client.LoginAsync("admin", "adminadmin");

            var torrentPath = Path.Combine(Utils.TorrentsFolder, "ubuntu-16.04.4-desktop-amd64.iso.torrent");
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>(torrentPath);
            
            var addRequest = new AddTorrentFilesRequest(torrentPath) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);
            
            await Task.Delay(1000);

            var trackers = await Client.GetTorrentTrackersAsync(torrent.OriginalInfoHash.ToLower());
            trackers.Should().NotBeNull();

            var trackerUrls = trackers.Select(t => t.Url.AbsoluteUri).ToList();
            trackerUrls.Should().BeEquivalentTo(torrent.Trackers.SelectMany(x => x));
        }
        
        [Fact]
        public async Task GetTorrentTrackersUnknown()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var trackers = await Client.GetTorrentTrackersAsync("0000000000000000000000000000000000000000");
            trackers.Should().BeNull(because: "torrent with this hash has not been added");
        }
        
        #endregion

        #region GetTorrentWebSeedsAsync

        [Fact]
        public async Task GetTorrentWebSeeds()
        {
            await Client.LoginAsync("admin", "adminadmin");

            var torrentPath = Path.Combine(Utils.TorrentsFolder, "ubuntu-16.04.4-desktop-amd64.iso.torrent");
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>(torrentPath);
            
            var addRequest = new AddTorrentFilesRequest(torrentPath) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);
  
            await Task.Delay(1000);
          
            var webSeeds = await Client.GetTorrentWebSeedsAsync(torrent.OriginalInfoHash.ToLower());
            webSeeds.Should().BeEmpty();
        }

        [Fact]
        public async Task GetTorrentWebSeedsUnknown()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var webSeeds = await Client.GetTorrentWebSeedsAsync("0000000000000000000000000000000000000000");
            webSeeds.Should().BeNull(because: "torrent with this hash has not been added");
        }
        
        #endregion

        #region GetTorrentPiecesStatesAsync/GetTorrentPiecesHashesAsync

        [Fact]
        public async Task GetTorrentPiecesAndStates()
        {
            await Client.LoginAsync("admin", "adminadmin");

            var torrentPath = Path.Combine(Utils.TorrentsFolder, "ubuntu-16.04.4-desktop-amd64.iso.torrent");
            var parser = new BencodeParser();
            var torrent = parser.Parse<Torrent>(torrentPath);
            
            var addRequest = new AddTorrentFilesRequest(torrentPath) { Paused = true };
            await Client.AddTorrentsAsync(addRequest);

            var torrentHash = torrent.OriginalInfoHash.ToLower();
            var hashes = await Client.GetTorrentPiecesHashesAsync(torrentHash);
            hashes.Should().NotBeNull().And.HaveCount(torrent.NumberOfPieces);
            hashes.Should().Equal(GetHashes());
 
            await Task.Delay(1000);
           
            var states = await Client.GetTorrentPiecesStatesAsync(torrentHash);
            states.Should().NotBeNull().And.HaveCount(torrent.NumberOfPieces);           
            
            IEnumerable<string> GetHashes()
            {
                var piecesAsHex = torrent.PiecesAsHexString;
                var length = piecesAsHex.Length / torrent.NumberOfPieces;
                for (int offset = 0; offset < piecesAsHex.Length; offset += length)
                {
                    yield return piecesAsHex.Substring(offset, length).ToLower();
                }
            }
        }

        [Fact]
        public async Task GetTorrentPiecesAndStatesUnknown()
        {
            await Client.LoginAsync("admin", "adminadmin");
            var hashes = await Client.GetTorrentPiecesHashesAsync("0000000000000000000000000000000000000000");
            hashes.Should().BeNull(because: "torrent with this hash has not been added");
            var states = await Client.GetTorrentPiecesStatesAsync("0000000000000000000000000000000000000000");
            states.Should().BeNull(because: "torrent with this hash has not been added");
        }
        
        #endregion
    }
}
