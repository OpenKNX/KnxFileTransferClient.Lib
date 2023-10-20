namespace KnxFileTransferClient.Lib;

public class FileTransferException : Exception
{
    public int ErrorCode { get; }
    private static Dictionary<int, string> Messages = new Dictionary<int, string>() {
        { 0x02, "Formatting of the file system has failed" },
        { 0x04, "Parameter pkg is greater than the allowed resultLength from Device" },
        //reserve
        { 0x41, "File already open" },
        { 0x42, "File can't be opened" },
        { 0x43, "File not opened" },
        { 0x44, "Deleting of the file failed" },
        { 0x45, "Renaming of the file failed" },
        { 0x46, "The file can't seek to position" },
        { 0x47, "File could not be written completely" },
        //reserve
        { 0x81, "Dir already open" },
        { 0x82, "Dir can't be opened" },
        { 0x83, "Dir not opened" },
        { 0x84, "Deleting of the folder failed" },
        { 0x85, "Creation of the folder failed" },
    };

    public FileTransferException(int code) : base(Messages[code])
    {
        ErrorCode = code;
    }

    public FileTransferException(string message, int code)
        : base(message)
    {
        ErrorCode = code;
    }

    public FileTransferException(string message, Exception inner, int code)
        : base(message, inner)
    {
        ErrorCode = code;
    }
}