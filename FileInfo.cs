using System.Diagnostics;
using System.Text;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.Messages.Response;

namespace KnxFileTransferClient.Lib;

public class FileInfo
{
    public int Size { get; set; }
    public byte[] CRC23 { get; set; } = new byte[0];

    public FileInfo(int size, byte[] crc23)
    {
        Size = size;
        CRC23 = crc23;
    }

    public string GetCrc()
    {
        return BitConverter.ToString(CRC23).Replace("-", "");
    }
}