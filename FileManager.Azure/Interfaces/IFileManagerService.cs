using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using FileManager.Azure.Dtos;
using FileManager.Azure.Models;

namespace FileManager.Azure.Interfaces
{
    public interface IFileManagerService
    {
        Task<string> AddFolder(string path, string newFolder);
        Task<BlobDto> GetFile(string path);
        Task<BlobDto> GetFolder(string path);
        BlobDto GetRootFolder();
        Task<bool> FileExists(string path);
        Task<List<BlobDto>> DeleteFile(string path, bool removeLease = false);
        Task<BlobDto> AddFile(string path, string contentType, string name, byte[] file);
        Task<IEnumerable<BlobDto>> GetFolderFiles(string path);
        Task<IEnumerable<BlobDto>> GetChildFolders(string prefix);
        Task<BlobDto> RenameFolder(BlobDto folder, string newName);
        Task<BlobDto> RenameFile(BlobDto file, string newName);
        Task<BlobDto> ReplaceFile(BlobDto file, Stream postedFile);
        Task<byte[]> GetFileBytes(string path);
        Task<BlobDto> MoveFolder(BlobDto folder, string path);
        Task<BlobDto> MoveFile(BlobDto file, string path);
        Task<SummaryInfo> GetSummaryInfo();
        Task<BlobContainerClient> GetContainer();
    }
}