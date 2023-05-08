using System.Text;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.Messages.Response;

namespace KnxFtpClient.Lib;

public class FtpClient
{
    private BusDevice device;
    private const int ObjectIndex = 159;


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

    public delegate void ProcessChangedHandler(int percent, int speed, int time);
    public event ProcessChangedHandler? ProcessChanged;

    public delegate void ErrorHandler(string message);
    public event ErrorHandler? OnError;


    public FtpClient(BusDevice _device) => device = _device;



    private long procSize = 0;
    private long procPos = 0;
    private DateTime procTime;
    private List<int> procSpeed = new List<int>();

    private void HandleProcess(int length)
    {
        if(ProcessChanged == null) return;

        procPos += length;
        int perc = (int)Math.Floor(procPos / (double)procSize);

        double time = (DateTime.Now - procTime).TotalMilliseconds;
        int speed = (int)Math.Floor(length / time / 1000);
        procSpeed.Add(speed);

        if(procSpeed.Count > 5)
            procSpeed.RemoveAt(0);

        int x = 0;
        foreach(int s in procSpeed)
            x += s;

        x = (int)(x / procSpeed.Count);

        int left = (int)Math.Floor((procSize - procPos) * (double)x);

        ProcessChanged?.Invoke(perc, x, left);
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
        procSpeed.Clear();
        procSize = stream.Length;
        procPos = 0;
        procTime = DateTime.Now;
        short sequence = 0;
        List<byte> data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(sequence));
        data.Add((byte)length);
        data.AddRange(UTF8Encoding.UTF8.GetBytes(path + char.MinValue));
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileUpload, data.ToArray(), true);
        sequence++;

        if(res.Data[0] != 0x00)
            throw new FtpException(res.Data[0]);


        int errorCount = 0;
        while(true)
        {
            byte[] buffer = new byte[length - 5];
            int readed = stream.Read(buffer, 0, length - 5);

            data.Clear();
            data.AddRange(BitConverter.GetBytes(sequence));
            data.AddRange(buffer);

            try
            {
                res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileUpload, data.ToArray(), true);

                if(res.Data[0] != 0x00)
                    throw new FtpException(res.Data[0]);

                int crcreq = CRC16.Get(data.ToArray());
                int crcresp = (res.Data[3] << 8) | res.Data[4];

                if (crcreq != crcresp)
                    throw new Exception($"Falscher CRC (Req: {crcreq:X4} / Res: {crcresp:X4})");
            }
            catch(FtpException ex)
            {
                throw ex;
            }
            catch(Exception ex) 
            {
                errorCount++;

                OnError?.Invoke(ex.Message);

                if(errorCount > 20)
                    throw new Exception("To many errors");

                continue;
            }

            sequence++;
            HandleProcess(length - 5);

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
        procSpeed.Clear();
        procPos = 0;
        procTime = DateTime.Now;
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

        int errorCount = 0;
        while(true)
        {
            try
            {
                res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileDownload, BitConverter.GetBytes(sequence), true);
            
                if(res.Data[0] != 0x00)
                    throw new FtpException(res.Data[0]);

                    
                int crcreq = CRC16.Get(data.ToArray());
                int crcresp = (res.Data[length - 1] << 8) | res.Data[length];

                if (crcreq != crcresp)
                    throw new Exception($"Falscher CRC (Req: {crcreq:X4} / Res: {crcresp:X4})");
            }
            catch(FtpException ex)
            {
                throw ex;
            }
            catch(Exception ex)
            {
                errorCount++;

                OnError?.Invoke(ex.Message);

                if(errorCount > 20)
                    throw new Exception("To many errors");

                continue;
            }
            
            sequence++;
            stream.Write(res.Data, 3, res.Data.Length - 5);
            HandleProcess(length - 5);

            if(res.Data.Length - 5 < length)
            {
                stream.Flush();
                return;
            }
        }
    }

    public async Task FileDelete(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtpCommands.FileDelete, buffer, true);

        if(res.Data[0] != 0x00)
            throw new FtpException(res.Data[0]);
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
