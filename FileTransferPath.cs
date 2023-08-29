namespace KnxFileTransferClient.Lib;

public class FileTransferPath
{
    public string Name { get; set; }
    public bool IsFile { get; set; }

    public FileTransferPath(string name, bool isFile)
    {
        Name = name;
        IsFile = isFile;
    }
}