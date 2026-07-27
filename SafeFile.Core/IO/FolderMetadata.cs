using System.Text;
using System.Text.Json;

namespace SafeFile.Core.IO;

public sealed class FolderMetadata
{
    public string SourcePath { get; set; } = string.Empty;
    public DateTime EncryptedAt { get; set; }
    public List<FileEntry> Files { get; set; } = new();

    public byte[] Serialize()
    {
        var json = JsonSerializer.Serialize(this);
        return Encoding.UTF8.GetBytes(json);
    }

    public static FolderMetadata Deserialize(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<FolderMetadata>(json) ?? new();
    }
}

public sealed class FileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string EncryptedFileName { get; set; } = string.Empty;
}
