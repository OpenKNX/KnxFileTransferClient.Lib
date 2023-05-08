namespace KnxFtpClient.Lib;

public class FtpPath
{
    public string Name { get; set; }
    public bool IsFile { get; set; }

    public FtpPath(string name, bool isFile)
    {
        Name = name;
        IsFile = isFile;
    }
}