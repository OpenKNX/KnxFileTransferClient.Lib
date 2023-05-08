using System.Text;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.Messages.Response;

namespace KnxFtpClient.Lib;

public class FtpClient
{
    private BusDevice device;
    private const int ObjectIndex = 159;

    private long procSize = 0;
    private List<int> averageSpeed = new List<int>();


    public enum FtpCommands
    {
        Format,
        Exists,
        Rename,
        FileUpload = 40,
        FileDownload,
        FileDelete,
        DirList = 80,
        DirCreate,
        DirDelete
    }

    public delegate void ProcessChangedHandler(double percent, double speed);
    public event ProcessChangedHandler ProcessChanged;


    public FtpClient(BusDevice _device) => device = _device;


    private void HandleProcess(int lengt)
    {

    }

    public async Task Format()
    {
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.Format, null, true);

        if(res.Data[0] != 0x00)        
            throw new FtpException(res.Data[0]);
    }

    public async Task<bool> Exists(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.Exists, buffer, true);

        if(res.Data[0] == 0x00)
            return res.Data[1] == 0x01;
        
        throw new FtpException(res.Data[0]);
    }

    public async Task Rename(string path, string newpath)
    {
        List<byte> data = new List<byte>();
        data.AddRange(UTF8Encoding.UTF8.GetBytes(path + char.MinValue));
        data.AddRange(UTF8Encoding.UTF8.GetBytes(newpath + char.MinValue));
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.Rename, data.ToArray(), true);

        if(res.Data[0] != 0x00)
            throw new FtpException(res.Data[0]);
    }

    public async Task FileUpload(string path, byte[] file, int length)
    {
        using(MemoryStream stream = new MemoryStream(file))
            await FileUpload(path, stream, length);
    }

    public async Task FileUpload(string path, string file, int length)
    {
        using(FileStream stream = File.Open(file, FileMode.Open))
            await FileUpload(path, stream, length);
    }

    public async Task FileUpload(string path, Stream stream, int length)
    {
        averageSpeed.Clear();
        procSize = stream.Length;
        short sequence = 0;
        List<byte> data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(sequence));
        data.Add((byte)length);
        data.AddRange(UTF8Encoding.UTF8.GetBytes(path + char.MinValue));
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileUpload, data.ToArray(), true);
        sequence++;

        while(true)
        {
            if(res.Data[0] != 0x00)
                throw new FtpException(res.Data[0]);

            byte[] buffer = new byte[length - 5];
            int readed = stream.Read(buffer, 0, length - 5);

            data.Clear();
            data.AddRange(BitConverter.GetBytes(sequence));
            data.AddRange(buffer);

            res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileDownload, data.ToArray(), true);
            HandleProcess(length - 5);
            sequence++;

            if(readed < length -5)
                break;
        }
    }

    public async Task FileDownload(string path, byte[] file, int length)
    {
        using(MemoryStream stream = new MemoryStream(file))
            await FileDownload(path, stream, length);
    }

    public async Task FileDownload(string path, string file, int length)
    {
        using(FileStream stream = File.Open(file, FileMode.Open))
            await FileDownload(path, stream, length);
    }

    public async Task FileDownload(string path, Stream stream, int length)
    {
        averageSpeed.Clear();
        short sequence = 0;
        List<byte> data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(sequence));
        data.Add((byte)length);
        data.AddRange(UTF8Encoding.UTF8.GetBytes(path + char.MinValue));
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileDownload, data.ToArray(), true);
        sequence++;

        if(res.Data[0] != 0x00)
            throw new FtpException(res.Data[0]);

        procSize = Convert.ToUInt32(res.Data.Skip(1).Take(4));

        while(true)
        {
            res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileDownload, BitConverter.GetBytes(sequence), true);
            sequence++;

            if(res.Data[0] != 0x00)
                throw new FtpException(res.Data[0]);

            stream.Write(res.Data, 3, res.Data.Length - 5);

            if(res.Data.Length - 5 < length)
            {
                stream.Flush();
                return;
            }
        }
    }

    public async Task<List<FtpPath>> List(string path)
    {
        List<FtpPath> list = new List<FtpPath>();
        byte[] data = ASCIIEncoding.ASCII.GetBytes(path + char.MinValue);
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.DirList, data, true);

        bool hasData = true;
        while(hasData)
        {
            if(res.Data[0] != 0x00)
                throw new FtpException(res.Data[0]);

            string name = ASCIIEncoding.ASCII.GetString(res.Data.Skip(2).ToArray());

            switch(res.Data[1])
            {
                case 0x00:
                    hasData = false;
                    break;
                    
                case 0x01:
                    list.Add(new FtpPath(name, true));
                    break;
                    
                case 0x02:
                    list.Add(new FtpPath(name, false));
                    break;
            }

            if(hasData)
                res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.DirList, null, true);
        }

        return list;
    }

    public async Task DirCreate(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.DirCreate, buffer, true);

        if(res.Data[0] != 0x00)
            throw new FtpException(res.Data[0]);
    }

    public async Task DirDelete(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.DirDelete, buffer, true);

        if(res.Data[0] != 0x00)
            throw new FtpException(res.Data[0]);
    }
}
