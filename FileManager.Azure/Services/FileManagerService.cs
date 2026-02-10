using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FileManager.Azure.Dictionary;
using FileManager.Azure.Dtos;
using FileManager.Azure.Helpers;
using FileManager.Azure.Interfaces;
using FileManager.Azure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FileManager.Azure.Services
{
    public class FileManagerService : IFileManagerService
    {
        private readonly StorageOptions _storageOptions;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly string _container;

        public FileManagerService(IOptions<StorageOptions> mediaConfig, IHttpContextAccessor httpContextAccessor, string container)
        {
            _httpContextAccessor = httpContextAccessor;
            _container = container;
            _storageOptions = mediaConfig.Value;
        }

        /// <summary>
        /// Addes a folder with a temp file
        /// </summary>
        /// <param name="path"></param>
        /// <param name="newFolder"></param>
        /// <returns></returns>
        public async Task<string> AddFolder(string path, string newFolder)
        {
            string tempFile = Path.GetTempFileName();

            Random random = new Random();
            var uploadedBytes = Array.Empty<byte>();
            random.NextBytes(uploadedBytes);

            File.WriteAllBytes(tempFile, uploadedBytes);

            string tempFileName = Path.GetFileName(tempFile);
            await AddFile($"{path}{newFolder}/{tempFileName}", "application/pdf", Path.GetFileNameWithoutExtension(tempFileName),
                uploadedBytes);

            return $"{path}{newFolder}/";
        }

        /// <summary>
        /// Gets a file for the given path, will return null if it doesn't exit
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<BlobDto> GetFile(string path)
        {
            var blobClient = await GetBlobClient(path);
            if (!await blobClient.ExistsAsync()) return null;

            // Get the BlobProperties
            BlobProperties properties = await blobClient.GetPropertiesAsync();

            return new BlobDto
            {
                ContentType = properties.ContentType,
                DateModified = properties.LastModified,
                DateCreated = properties.CreatedOn,
                FileSize = properties.ContentLength,
                Name = properties.GetFileName(blobClient),
                BlobType = AzureBlobType.File,
                StoragePath = blobClient.Uri.ToString(),
                Path = path
            };
        }

        /// <summary>
        /// Gets the root folder for the current user
        /// </summary>
        /// <returns></returns>
        public BlobDto GetRootFolder()
        {
            Claim rootFolderClaim = _httpContextAccessor.HttpContext?.User.FindFirst("RootFolder");
            if (rootFolderClaim == null)
            {
                return new BlobDto
                {
                    Name = "Root",
                    StoragePath = "/",
                    BlobType = AzureBlobType.Folder
                };
            }

            return new BlobDto
            {
                Name = rootFolderClaim.Value,
                BlobType = AzureBlobType.Folder
            };
        }

        /// <summary>
        /// Deletes a file or folder based on the path specified
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<List<BlobDto>> DeleteFile(string path)
        {
            var container = await GetContainer();

            List<BlobDto> deletedFiles = new List<BlobDto>();
            if (Path.HasExtension(path))
            {
                var blobClient = await GetBlobClient(path);
                if (_storageOptions.TakeSnapshots)
                {
                    await blobClient.CreateSnapshotAsync();
                }

                BlobProperties properties = await blobClient.GetPropertiesAsync();
                deletedFiles.Add(new BlobDto
                {
                    ContentType = properties.ContentType,
                    DateModified = properties.LastModified,
                    DateCreated = properties.CreatedOn,
                    FileSize = properties.ContentLength,
                    Name = properties.GetFileName(blobClient),
                    BlobType = AzureBlobType.File,
                    StoragePath = blobClient.Uri.ToString(),
                    Path = blobClient.Name
                });

                await blobClient.DeleteIfExistsAsync();
            }
            else
            {
                var blobs = container.GetBlobsAsync(new GetBlobsOptions()
                {
                    Prefix = path
                });

                List<Task> tasks = new List<Task>();
                var pages = blobs.AsPages();
                await foreach (var page in pages)
                {
                    foreach (var blob in page.Values)
                    {
                        var blobClient = await GetBlobClient(blob.Name);

                        deletedFiles.Add(new BlobDto
                        {
                            ContentType = blob.Properties.ContentType,
                            DateModified = blob.Properties.LastModified,
                            DateCreated = blob.Properties.CreatedOn,
                            FileSize = (long)blob.Properties.ContentLength,
                            Name = blob.GetFileName(),
                            BlobType = AzureBlobType.File,
                            StoragePath = blobClient.Uri.ToString(),
                            Path = blob.Name
                        });

                        tasks.Add(blobClient.DeleteIfExistsAsync());
                    }
                }


                Task.WaitAll(tasks.ToArray());
            }

            return deletedFiles;
        }

        /// <summary>
        /// Adds a file for the given content type and name
        /// </summary>
        /// <param name="path"></param>
        /// <param name="contentType"></param>
        /// <param name="name"></param>
        /// <param name="file"></param>
        /// <returns></returns>
        public async Task<BlobDto> AddFile(string path, string contentType, string name, byte[] file)
        {
            BlobClient blobClient = await GetBlobClient(path);

            // Create a BlobHttpHeaders object to set the properties
            BlobHttpHeaders blobHttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType,
            };

            // Create a new Dictionary to hold the metadata key-value pairs
            Dictionary<string, string> metadata = new Dictionary<string, string>
            {
                { "DateCreated", DateTime.UtcNow.ToString("O") },
                { "FileName", name },
            };

            await blobClient.UploadAsync(new BinaryData(file), new BlobUploadOptions()
            {
                HttpHeaders = blobHttpHeaders,
                Metadata = metadata
            });

            // Get the BlobProperties
            BlobProperties properties = await blobClient.GetPropertiesAsync();
            return new BlobDto
            {
                ContentType = properties.ContentType,
                DateModified = properties.LastModified,
                DateCreated = properties.CreatedOn,
                FileSize = properties.ContentLength,
                Name = properties.GetFileName(blobClient),
                BlobType = AzureBlobType.File,
                StoragePath = blobClient.Uri.ToString(),
                Path = blobClient.Name
            };
        }

        /// <summary>
        /// Gets all of the files in a folder
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<IEnumerable<BlobDto>> GetFolderFiles(string path)
        {
            var container = await GetContainer();
            var blobs = container.GetBlobsAsync(new GetBlobsOptions()
            {
                Prefix = path
            });

            List<BlobDto> files = new List<BlobDto>();
            await foreach (var blob in blobs)
            {
                var client = await GetBlobClient(blob.Name);
                files.Add(new BlobDto
                {
                    ContentType = blob.Properties.ContentType,
                    DateModified = blob.Properties.LastModified,
                    DateCreated = blob.Properties.CreatedOn,
                    FileSize = (long)blob.Properties.ContentLength,
                    Name = blob.GetFileName(),
                    BlobType = AzureBlobType.File,
                    StoragePath = client.Uri.ToString(),
                    Path = blob.Name
                });
            }

            return files;
        }

        /// <summary>
        /// Gets a folder for the given path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<BlobDto> GetFolder(string path)
        {
            var directory = await GetBlobClient(path);

            return new BlobDto
            {
                BlobType = AzureBlobType.Folder,
                StoragePath = directory.Uri.ToString(),
                Path = directory.Name,
                Name = GetDirectoryName(path)
            };
        }

        /// <summary>
        /// Gets all of the child folders from a given path
        /// </summary>
        /// <param name="prefix"></param>
        /// <returns></returns>
        public async Task<IEnumerable<BlobDto>> GetChildFolders(string prefix)
        {
            var container = await GetContainer();
            var blobs = container.GetBlobsByHierarchyAsync(new GetBlobsByHierarchyOptions()
            {
                Prefix = prefix,
                Delimiter = "/"
            });

            List<BlobDto> folders = new List<BlobDto>();

            // List blobs with the given prefix
            await foreach (BlobHierarchyItem blobHierarchyItem in blobs)
            {
                if (blobHierarchyItem.IsPrefix)
                {
                    var blobClient = await GetBlobClient(blobHierarchyItem.Prefix);
                    folders.Add(new BlobDto
                    {
                        BlobType = AzureBlobType.Folder,
                        StoragePath = blobClient.Uri.ToString(),
                        Path = blobHierarchyItem.Prefix,
                        Name = GetDirectoryName(blobHierarchyItem.Prefix)
                    });
                }
            }

            return folders;
        }

        private string GetDirectoryName(string uri)
        {
            if (uri[uri.Length - 1] == '/')
            {
                uri = uri.Substring(0, uri.Length - 1);
            }

            return uri.Split('/').Last();
        }

        /// <summary>
        /// Renames the given folder
        /// </summary>
        /// <param name="folder"></param>
        /// <param name="newName"></param>
        /// <returns></returns>
        public async Task<BlobDto> RenameFolder(BlobDto folder, string newName)
        {
            var container = await GetContainer();

            string oldPath = folder.Path;
            string newPath = oldPath.Replace(folder.Name, newName);

            var pages = container.GetBlobsByHierarchyAsync(new GetBlobsByHierarchyOptions()
            {
                Prefix = folder.Path
            });
            var tasks = new List<Task>();
            // List the blobs within the folder
            await foreach (BlobHierarchyItem blobItem in pages)
            {
                // Get the name of the blob within the folder
                string blobName = blobItem.Blob.Name;

                // Calculate the new blob name by replacing the folder name
                string newBlobName = blobName.Replace(oldPath, newPath);

                // Create a reference to the original blob
                BlobClient originalBlobClient = container.GetBlobClient(blobName);

                // Create a reference to the new blob
                BlobClient newBlobClient = container.GetBlobClient(newBlobName);

                // Start the copy operation from the original blob to the new blob
                await newBlobClient.StartCopyFromUriAsync(originalBlobClient.Uri);

                // Delete the original blob
                await originalBlobClient.DeleteIfExistsAsync();
            }

            var blobClient = await GetBlobClient(newPath);

            folder.Path = newPath;
            folder.StoragePath = blobClient.Uri.ToString();
            folder.DateModified = DateTime.UtcNow;

            return folder;
        }

        /// <summary>
        /// Renames the given file
        /// </summary>
        /// <param name="file"></param>
        /// <param name="newName"></param>
        /// <returns></returns>
        public async Task<BlobDto> RenameFile(BlobDto file, string newName)
        {
            string oldPath = file.Path;
            string newPath = oldPath.Replace($"{file.Name}{Path.GetExtension(file.Path)}", newName);

            var blob = await UpdateFilePath(oldPath, newPath);

            BlobProperties properties = await blob.GetPropertiesAsync();
            properties.Metadata.Remove("FileName");
            properties.Metadata.Add("FileName", Path.GetFileNameWithoutExtension(newName));
            await blob.SetMetadataAsync(properties.Metadata);

            file.Name = newName;
            file.StoragePath = blob.Uri.ToString();

            return file;
        }

        /// <summary>
        /// Replaces a files content and takes a snapshot if take snapshots is enabled
        /// </summary>
        /// <param name="file"></param>
        /// <param name="postedFile"></param>
        /// <returns></returns>
        public async Task<BlobDto> ReplaceFile(BlobDto file, Stream postedFile)
        {
            var blob = await GetBlobClient(file.Path);
            if (_storageOptions.TakeSnapshots)
            {
                await blob.CreateSnapshotAsync();
            }

            await blob.UploadAsync(postedFile);

            BlobProperties properties = await blob.GetPropertiesAsync();
            file.FileSize = properties.ContentLength;
            file.DateModified = DateTime.UtcNow;

            return file;
        }

        /// <summary>
        /// Gets a files bytes from azure storage
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<byte[]> GetFileBytes(string path)
        {
            BlobClient blob = await GetBlobClient(path);

            BlobProperties properties = await blob.GetPropertiesAsync();
            long fileByteLength = properties.ContentLength;

            byte[] myByteArray = new byte[fileByteLength];
            using (var stream = new MemoryStream(myByteArray))
            {
                await blob.DownloadToAsync(stream);
            }

            return myByteArray;
        }

        /// <summary>
        /// Moves a folder with its contents to the path given
        /// </summary>
        /// <param name="folder"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<BlobDto> MoveFolder(BlobDto folder, string path)
        {
            var container = await GetContainer();

            if (path[path.Length - 1] != '/')
            {
                path = path + "/";
            }

            string oldPath = folder.Path;
            string newPath = oldPath.Replace(folder.Name, path);

            var blobs = container.GetBlobsAsync(new GetBlobsOptions()
            {
                Prefix = folder.Path
            });
            // List the blobs within the folder
            await foreach (BlobItem blobItem in blobs)
            {
                // Get the name of the blob within the folder
                string blobName = blobItem.Name;

                // Calculate the new blob name by replacing the folder name
                string newBlobName = blobName.Replace(oldPath, newPath);

                // Create a reference to the original blob
                BlobClient originalBlobClient = container.GetBlobClient(blobName);

                // Create a reference to the new blob
                BlobClient newBlobClient = container.GetBlobClient(newBlobName);

                // Start the copy operation from the original blob to the new blob
                await newBlobClient.StartCopyFromUriAsync(originalBlobClient.Uri);

                // Delete the original blob
                await originalBlobClient.DeleteIfExistsAsync();
            }

            var blobClient = await GetBlobClient(newPath);

            folder.Path = newPath;
            folder.StoragePath = blobClient.Uri.ToString();
            folder.DateModified = DateTime.UtcNow;

            return folder;
        }

        /// <summary>
        /// Updates the given files path to the new path specified, path should have leading and trailing forward slashes (ex /temp/)
        /// </summary>
        /// <param name="file"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<BlobDto> MoveFile(BlobDto file, string path)
        {
            string newPath = $"{path}{file.Name}{Path.GetExtension(file.Path)}";
            file.DateModified = DateTime.UtcNow;

            await UpdateFilePath(file.Path, newPath);

            return file;
        }

        /// <summary>
        /// Gets a summary of the current users number of files, space used and size limit
        /// </summary>
        /// <returns></returns>
        public async Task<SummaryInfo> GetSummaryInfo()
        {
            var container = await GetContainer();
            var folders = new List<BlobDto>();
            var files = new List<BlobDto>();

            var blobs = container.GetBlobsByHierarchy();
            foreach (var blobItem in blobs)
            {
                if (!blobItem.IsBlob)
                {
                    var blobClient = await GetBlobClient(blobItem.Prefix);
                    folders.Add(new BlobDto
                    {
                        BlobType = AzureBlobType.Folder,
                        StoragePath = blobClient.Uri.ToString()
                    });
                }
                else
                {
                    var blobClient = await GetBlobClient(blobItem.Prefix);

                    BlobProperties properties = await blobClient.GetPropertiesAsync();
                    files.Add(new BlobDto
                    {
                        ContentType = properties.ContentType,
                        DateModified = properties.LastModified,
                        DateCreated = properties.CreatedOn,
                        FileSize = properties.ContentLength,
                        Name = properties.GetFileName(blobClient),
                        BlobType = AzureBlobType.File,
                        StoragePath = blobClient.Uri.ToString(),
                        Path = blobItem.Prefix
                    });
                }
            }

            return new SummaryInfo
            {
                Folders = folders.Count,
                Size = files.Sum(x => x.FileSize),
                Files = files.Count,
                SizeLimit = GetContainerSizeLimit()
            };
        }

        /// <summary>
        /// Indicates if the file exists for any given path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<bool> FileExists(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            if (path[0].Equals('/'))
            {
                path = path.Substring(1);
            }

            var blobClient = await GetBlobClient(path);
            return await blobClient.ExistsAsync();
        }

        private async Task<BlobClient> UpdateFilePath(string oldFilePath, string newFilePath)
        {
            var source = await GetBlobClient(oldFilePath);
            var target = await GetBlobClient(newFilePath);

            var operation = await target.StartCopyFromUriAsync(source.Uri);
            long result = await operation.WaitForCompletionAsync();

            if (result == 0)
                throw new Exception("Rename failed: " + newFilePath);

            await source.DeleteAsync();

            return target;
        }

        public async Task<BlobContainerClient> GetContainer()
        {
            var container = new BlobContainerClient(_storageOptions.StorageConnStr, _container);

            if (!await container.ExistsAsync())
            {
                await container.CreateAsync();
                await container.SetAccessPolicyAsync(PublicAccessType.Blob);
            }

            return container;
        }

        private async Task<BlobClient> GetBlobClient(string path)
        {
            if (path[0].Equals('/'))
            {
                path = path.Substring(1);
            }

            var container = await GetContainer();
            return container.GetBlobClient(path);
        }

        private long GetContainerSizeLimit()
        {
            Claim sizeLimitClaim =
                _httpContextAccessor.HttpContext?.User.FindFirst("BlobContainerSizeLimit");

            long maxLimit = (long)2.5e+17;
            long limit = sizeLimitClaim == null ? (long)2.5e+17 : Convert.ToInt64(sizeLimitClaim.Value);

            if (limit > maxLimit)
            {
                throw new Exception("Container size limit exceeds what azure supports (500TB^2)");
            }

            return limit;
        }
    }
}