using Azure.Storage.Blobs.Models;
using System.IO;
using System.Web;
using Azure.Storage.Blobs;

namespace FileManager.Azure.Helpers;

public static class StringHelpers
{
    public static string GetFileName(this BlobItem blob)
    {
        string fileName = (blob.Metadata != null && blob.Metadata.TryGetValue("FileName", out var metadataFileName))
            ? HttpUtility.UrlDecode(metadataFileName)
            : Path.GetFileName(blob.Name);
        return fileName;
    }

    public static string GetFileName(this BlobProperties blobProperties, BlobClient blobClient)
    {
        string fileName = (blobProperties.Metadata != null && blobProperties.Metadata.TryGetValue("FileName", out var metadataFileName))
            ? HttpUtility.UrlDecode(metadataFileName)
            : Path.GetFileName(blobClient.Name);
        return fileName;
    }
}