// Pseudocode plan:
// 1. Scan the file for all NUnit assertion usages.
// 2. Replace `Assert.IsTrue`/`Assert.IsFalse` with `Assert.That(..., Is.True/Is.False)`.
// 3. Replace `Assert.NotNull`/`Assert.IsNull` with `Assert.That(..., Is.Not.Null/Is.Null)`.
// 4. Replace `Assert.AreEqual` with `Assert.That(actual, Is.EqualTo(expected))`.
// 5. Keep existing `Assert.That` calls unchanged unless needed.
// 6. Output the full updated file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using FileManager.Azure.Dictionary;
using FileManager.Azure.Dtos;
using FileManager.Azure.Helpers;
using FileManager.Azure.Interfaces;
using FileManager.Azure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace FileManager.Azure.Tests
{
    [TestFixture]
    public class StorageTests
    {
        private IFileManagerService _fileManagerService;
        private readonly List<string> _tempFiles = new List<string>();

        [SetUp]
        public void Init()
        {
            var loggingFactoryMock = new Mock<ILoggerFactory>();

            var fakeIdentity = new GenericIdentity("User");
            fakeIdentity.AddClaim(new Claim("BlobContainer", "test-user-container"));
            var principal = new GenericPrincipal(fakeIdentity, null);

            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            httpContextAccessorMock.SetupGet(x => x.HttpContext.User).Returns(() => principal);

            var configMock = new Mock<IOptions<StorageOptions>>();
            configMock.SetupGet(x => x.Value).Returns(() => new StorageOptions
            {
                StorageConnStr = "AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;DefaultEndpointsProtocol=http;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;"
            });

            var builder = new ConfigurationBuilder();

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddFileManager(builder.Build());
            serviceCollection.Add(new ServiceDescriptor(typeof(IHttpContextAccessor), httpContextAccessorMock.Object));
            serviceCollection.Add(new ServiceDescriptor(typeof(IOptions<StorageOptions>), configMock.Object));

            var provider = serviceCollection.BuildServiceProvider();

            _fileManagerService = provider.GetService<Func<string, IFileManagerService>>()("test-container");
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (var tempFile in _tempFiles)
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }

            var container = await _fileManagerService.GetContainer();
            await container.DeleteIfExistsAsync();
        }

        [Test]
        public async Task Test_Add_File()
        {
            string tempFile = CreateTempFile();
            var uploadedBytes = File.ReadAllBytes(tempFile);

            string name = Path.GetFileNameWithoutExtension(tempFile);
            string path = $"/temp/{name}.tmp";

            await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
            Assert.That(await _fileManagerService.FileExists(path), Is.True);
        }

        [Test]
        public async Task Test_Get_File()
        {
            string tempFile = CreateTempFile();
            var uploadedBytes = File.ReadAllBytes(tempFile);

            string name = Path.GetFileNameWithoutExtension(tempFile);
            string path = $"/temp/{name}.tmp";

            await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);

            var fileBlob = await _fileManagerService.GetFile(path);
            Assert.That(fileBlob, Is.Not.Null);
        }

        [Test]
        public async Task Test_Delete_File()
        {
            string tempFile = CreateTempFile();
            var uploadedBytes = File.ReadAllBytes(tempFile);

            string name = Path.GetFileNameWithoutExtension(tempFile);
            string path = $"/temp/{name}.tmp";

            await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
            Assert.That(await _fileManagerService.FileExists(path), Is.True);

            await _fileManagerService.DeleteFile(path);
            Assert.That(await _fileManagerService.FileExists(path), Is.False);
        }

        [Test]
        public async Task Test_Rename_File()
        {
            string tempFile = CreateTempFile();
            var uploadedBytes = File.ReadAllBytes(tempFile);

            string name = Path.GetFileNameWithoutExtension(tempFile);
            string path = $"/temp/{name}.tmp";

            var blobDto = await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
            Assert.That(await _fileManagerService.FileExists(path), Is.True);

            await _fileManagerService.RenameFile(blobDto, "my-new-name.tmp");

            var renamedBlogDto = await _fileManagerService.GetFile("/temp/my-new-name.tmp");
            Assert.That(renamedBlogDto, Is.Not.Null);
        }

        [Test]
        public async Task Test_Move_File()
        {
            string tempFile = CreateTempFile();
            var uploadedBytes = File.ReadAllBytes(tempFile);

            string name = Path.GetFileNameWithoutExtension(tempFile);
            string path = $"/temp/{name}.tmp";

            var blobDto = await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
            Assert.That(await _fileManagerService.FileExists(path), Is.True);

            await _fileManagerService.MoveFile(blobDto, "/temp2/");

            var movedBlobDto = await _fileManagerService.GetFile($"/temp2/{name}.tmp");
            Assert.That(movedBlobDto, Is.Not.Null);
        }

        [Test]
        public async Task Test_Return_Null_If_Not_Exists()
        {
            var blobDto = await _fileManagerService.GetFile("doesnt-exist.tmp");
            Assert.That(blobDto, Is.Null);
        }

        [Test]
        public async Task Test_Delete_Folder()
        {
            var files = GetFiles();

            foreach (var file in files)
            {
                var uploadedBytes = File.ReadAllBytes(file);

                string name = Path.GetFileNameWithoutExtension(file);
                string path = $"/temp/{name}.tmp";

                await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
                Assert.That(await _fileManagerService.FileExists(path), Is.True);
            }

            await _fileManagerService.DeleteFile("temp/");
            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string path = $"/temp/{name}.tmp";
                Assert.That(await _fileManagerService.FileExists(path), Is.False);
            }
        }

        [Test]
        public async Task Test_Get_Folder_Files()
        {
            var tempFiles = GetFiles();

            foreach (var file in tempFiles)
            {
                var uploadedBytes = File.ReadAllBytes(file);

                string name = Path.GetFileNameWithoutExtension(file);
                string path = $"/temp/{name}.tmp";

                await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
                Assert.That(await _fileManagerService.FileExists(path), Is.True);
            }

            var files = await _fileManagerService.GetFolderFiles("temp/");
            Assert.That(files.Count(), Is.EqualTo(5));
        }

        [Test]
        public async Task Test_Rename_Folder()
        {
            var tempFiles = GetFiles();

            foreach (var file in tempFiles)
            {
                var uploadedBytes = File.ReadAllBytes(file);

                string name = Path.GetFileNameWithoutExtension(file);
                string path = $"/temp/{name}.tmp";

                await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
                Assert.That(await _fileManagerService.FileExists(path), Is.True);
            }

            await _fileManagerService.RenameFolder(new BlobDto
            {
                Path = "temp/",
                BlobType = AzureBlobType.Folder,
                Name = "temp"
            }, "temp2");

            var files = await _fileManagerService.GetFolderFiles("temp2");
            Assert.That(files.Count(), Is.EqualTo(5));
        }

        [Test]
        public async Task Test_Move_Folder()
        {
            var tempFiles = GetFiles();

            foreach (var file in tempFiles)
            {
                var uploadedBytes = File.ReadAllBytes(file);

                string name = Path.GetFileNameWithoutExtension(file);
                string path = $"/temp/{name}.tmp";

                await _fileManagerService.AddFile(path, "application/pdf", name, uploadedBytes);
                Assert.That(await _fileManagerService.FileExists(path), Is.True);
            }

            await _fileManagerService.MoveFolder(new BlobDto
            {
                Path = "temp/",
                BlobType = AzureBlobType.Folder,
                Name = "temp"
            }, "temp2");

            var files = await _fileManagerService.GetFolderFiles("temp2/");
            Assert.That(files.Count(), Is.EqualTo(5));
        }

        [Test]
        public async Task Test_Get_Child_Folders()
        {
            for (var i = 0; i < 5; i++)
            {
                await _fileManagerService.AddFolder("temp/", $"folder{i}");
            }

            var files = await _fileManagerService.GetChildFolders("temp/");
            Assert.That(files.Count(), Is.EqualTo(5));
        }

        private string CreateTempFile()
        {
            string tempFile = Path.GetTempFileName();

            Random random = new Random();
            var uploadedBytes = new byte[128];
            random.NextBytes(uploadedBytes);

            File.WriteAllBytes(tempFile, uploadedBytes);

            return tempFile;
        }

        private List<string> GetFiles()
        {
            var files = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                files.Add(CreateTempFile());
            }

            _tempFiles.AddRange(files);
            return files;
        }
    }
}