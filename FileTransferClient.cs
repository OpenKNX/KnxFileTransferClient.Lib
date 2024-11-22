using System.Diagnostics;
using System.Text;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.Exceptions;
using Kaenx.Konnect.Messages.Response;

namespace KnxFileTransferClient.Lib;

public class FileTransferClient
{
    private BusDevice device;
    private const int ObjectIndex = 159;

    public enum FtmCommands
    {
        Format,
        Exists,
        Rename,
        FileUpload = 40,
        FileDownload,
        FileDelete,
        FileInfo,
        DirList = 80,
        DirCreate,
        DirDelete,
        Cancel = 90,
        GetVersion = 100
    }

    public delegate void ProcessChangedHandler(int percent, int speed, int time);
    public event ProcessChangedHandler? ProcessChanged;

    public delegate void PrintInfoHandler(string info);
    public event PrintInfoHandler? PrintInfo;

    public delegate void ErrorHandler(Exception exception);
    public event ErrorHandler? OnError;

    public FileTransferClient(BusDevice _device) => device = _device;

    public static int GetVersionMajor()
    {
        return typeof(FileTransferClient).Assembly.GetName().Version.Major;
    }

    public static int GetVersionMinor()
    {
        return typeof(FileTransferClient).Assembly.GetName().Version.Minor;
    }

    public static int GetVersionBuild()
    {
        return typeof(FileTransferClient).Assembly.GetName().Version.Build;
    }

    private long procSize = 0;
    private long procPos = 0;
    private DateTime procTime;
    private List<int> procSpeed = new List<int>();

    private void HandleProcess(int length)
    {
        procPos += length;
        int perc = (int)Math.Floor((procPos*100) / (double)procSize);

        double time = (DateTime.Now - procTime).TotalMilliseconds;
        procTime = DateTime.Now;
        int speed = (int)Math.Floor(length / (time / 1000));
        procSpeed.Add(speed);

        if(procSpeed.Count > 20)
            procSpeed.RemoveAt(0);

        int x = 0;
        foreach(int s in procSpeed)
            x += s;

        x = (int)(x / procSpeed.Count);

        int left = (int)Math.Floor((procSize - procPos) / (double)x);

        ProcessChanged?.Invoke(perc, x, left);
    }

    public async Task<string> CheckVersion()
    {
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.GetVersion, null, true);
        int major = BitConverter.ToInt16(new byte[] { res.Data[1], res.Data[0]});
        int minor = BitConverter.ToInt16(new byte[] { res.Data[3], res.Data[2]});
        int build = BitConverter.ToInt16(new byte[] { res.Data[5], res.Data[4]});
        string result = $"{major}.{minor}.{build}";

        if(major != GetVersionMajor())
            throw new Exception("Incompatible Remote MajorVersion: " + result);

        return result;
    }

    public async Task Format()
    {
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.Format, null, true);

        if(res.Data[0] != 0x00)        
            throw new FileTransferException(res.Data[0]);
    }

    public async Task<bool> Exists(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        if(device.MaxFrameLength < buffer.Length + 2)
            throw new Exception($"The Path is to long ({buffer.Length + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.Exists, buffer, true);

        if(res.Data[0] == 0x00)
            return res.Data[1] == 0x01;
        
        throw new FileTransferException(res.Data[0]);
    }

    public async Task Rename(string path, string newpath)
    {
        List<byte> data = new List<byte>();
        data.AddRange(UTF8Encoding.UTF8.GetBytes(path + char.MinValue));
        data.AddRange(UTF8Encoding.UTF8.GetBytes(newpath + char.MinValue));
        if(device.MaxFrameLength < data.Count + 2)
            throw new Exception($"Both Paths are to long ({data.Count + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.Rename, data.ToArray(), true);

        if(res.Data[0] != 0x00)
            throw new FileTransferException(res.Data[0]);
    }

    public async Task FileUpload(string path, byte[] file, int length, short start_sequence)
    {
        using(MemoryStream stream = new MemoryStream(file))
            await FileUpload(path, stream, length, start_sequence);
    }

    public async Task FileUpload(string local, string host, int length, short start_sequence)
    {
        using(FileStream stream = File.Open(local, FileMode.Open))
            await FileUpload(host, stream, length, start_sequence);
    }

    public async Task<FileInfo> FileInfo(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        if(device.MaxFrameLength < buffer.Length + 2)
            throw new Exception($"The Path is to long ({buffer.Length + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.FileInfo, buffer, true);

        if(res.Data[0] == 0x00)
        {
            int size = BitConverter.ToInt32(res.Data.Skip(1).Take(4).Reverse().ToArray(), 0);
            FileInfo info = new FileInfo(size, res.Data.Skip(5).Take(4).ToArray());
            PrintInfo?.Invoke($"File: {path} - Size: {size} bytes - CRC16: {info.GetCrc()}");
            return info;
        }
        else
            throw new FileTransferException(res.Data[0]);
    }

    public async Task FileUpload(string path, Stream stream, int length, short start_sequence)
    {
        Stopwatch sw = new Stopwatch();
        sw.Start();
        procSpeed.Clear();
        procSize = stream.Length;
        procPos = 0;
        procTime = DateTime.Now;
        short sequence = 0;
        List<byte> data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(sequence));
        data.Add((byte)length);
        data.AddRange(UTF8Encoding.UTF8.GetBytes(path + char.MinValue));

        if(device.MaxFrameLength < data.Count + 2)
            throw new Exception($"The Path is to long ({data.Count + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.FileUpload, data.ToArray(), true);
        sequence = start_sequence;
        sequence++;

        if(res.Data[0] != 0x00)
            throw new FileTransferException(res.Data[0]);


        int readed = 0;
        int errorCount = 0;
        while(true)
        {
            if(errorCount == 0)
            {
                byte[] buffer = new byte[length - 3];
                readed = stream.Read(buffer, 0, length - 3);

                if(readed == 0)
                    break;
                    
                data.Clear();
                data.AddRange(BitConverter.GetBytes(sequence));
                data.Add((byte)readed);
                data.AddRange(buffer);
            }

            try
            {
                res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.FileUpload, data.ToArray(), true);

                if(res.Data[0] != 0x00)
                    throw new FileTransferException(res.Data[0]);

                int crcreq = CRC16.Get(data.ToArray());
                int crcresp = (res.Data[3] << 8) | res.Data[4];

                if (crcreq != crcresp)
                    throw new Exception($"Falscher CRC (Req: {crcreq:X4} / Res: {crcresp:X4})");
            }
            catch(FileTransferException ex)
            {
                throw new Exception("FileTransferException",ex);
            }
            catch(DeviceNotConnectedException ex)
            {
                errorCount++;
                OnError?.Invoke(ex);
                if(errorCount > 3)
                    throw new Exception("To many errors");
                    
                await device.Connect();
                continue;
            }
            catch(Exception ex) 
            {
                errorCount++;

                OnError?.Invoke(ex);

                if(errorCount > 3)
                    throw new Exception("To many errors");

                continue;
            }

            errorCount = 0;
            sequence++;
            HandleProcess(readed);                
        }

        await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.FileUpload, new byte[] {0xFF, 0xFF}, true);
        sw.Stop();
        int xspeed = (int)(stream.Length / sw.Elapsed.TotalSeconds);
        PrintInfo?.Invoke($"Abgeschlossen in {sw.Elapsed.Minutes}:{sw.Elapsed.Seconds:D2} ({xspeed:D3} bytes/s)");
    }

    public async Task FileDownload(string path, byte[] file, int length)
    {
        using(MemoryStream stream = new MemoryStream(file))
            await FileDownload(path, stream, length);
    }

    public async Task FileDownload(string path, string file, int length)
    {
        using(FileStream stream = File.Open(file, FileMode.OpenOrCreate))
            await FileDownload(path, stream, length);
    }

    public async Task FileDownload(string path, Stream stream, int length)
    {
        Stopwatch sw = new Stopwatch();
        sw.Start();
        procSpeed.Clear();
        procPos = 0;
        procTime = DateTime.Now;
        short sequence = 0;
        List<byte> data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(sequence));
        data.Add((byte)length);
        data.AddRange(UTF8Encoding.UTF8.GetBytes(path + char.MinValue));
        
        if(device.MaxFrameLength < data.Count + 2)
            throw new Exception($"The Path is to long ({data.Count + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.FileDownload, data.ToArray(), true);
        sequence++;

        if(res.Data[0] != 0x00)
            throw new FileTransferException(res.Data[0]);

        procSize = BitConverter.ToInt32(res.Data.Skip(1).Take(4).ToArray(), 0);

        int errorCount = 0;
        while(true)
        {
            try
            {
                res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.FileDownload, BitConverter.GetBytes(sequence), true);
            
                if(res.Data[0] != 0x00)
                    throw new FileTransferException(res.Data[0]);
       
                int crcreq = CRC16.Get(res.Data.Skip(1).Take(res.Data[3] + 3).ToArray());
                int crcresp = (res.Data[res.Data.Count() - 2] << 8) | res.Data[res.Data.Count() -1];

                if (crcreq != crcresp)
                    throw new Exception($"Falscher CRC (Req: {crcreq:X4} / Res: {crcresp:X4})");
            }
            catch(FileTransferException ex)
            {
                throw ex;
            }
            catch(Exception ex)
            {
                errorCount++;

                OnError?.Invoke(ex);

                if(errorCount > 3)
                    throw new Exception("To many errors");

                continue;
            }
            
            sequence++;
            stream.Write(res.Data, 4, res.Data.Length - 6);
            stream.Flush();
            HandleProcess(length - 6);

            if(res.Data.Length < length)
            {
                stream.Flush();
                break;
            }
        }
        sw.Stop();
        int xspeed = (int)(stream.Length / sw.Elapsed.TotalSeconds);
        PrintInfo?.Invoke($"Abgeschlossen in {sw.Elapsed.Minutes}:{sw.Elapsed.Seconds:D2} ({xspeed:D3} bytes/s)");
    }

    public async Task FileDelete(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.FileDelete, buffer, true);

        if(res.Data[0] != 0x00)
            throw new FileTransferException(res.Data[0]);
    }

    public async Task<List<FileTransferPath>> List(string path)
    {
        List<FileTransferPath> list = new List<FileTransferPath>();
        byte[] data = ASCIIEncoding.ASCII.GetBytes(path + char.MinValue);
        
        if(device.MaxFrameLength < data.Length + 2)
            throw new Exception($"The Path is to long ({data.Length + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.DirList, data, true);

        bool hasData = true;
        while(hasData)
        {
            if(res.Data[0] != 0x00)
                throw new FileTransferException(res.Data[0]);

            string name = ASCIIEncoding.ASCII.GetString(res.Data.Skip(2).ToArray());

            switch(res.Data[1])
            {
                case 0x00:
                    hasData = false;
                    break;
                    
                case 0x01:
                    list.Add(new FileTransferPath(name, true));
                    break;
                    
                case 0x02:
                    list.Add(new FileTransferPath(name, false));
                    break;
            }

            if(hasData)
                res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.DirList, null, true);
        }

        return list;
    }

    public async Task DirCreate(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        if(device.MaxFrameLength < buffer.Length + 2)
            throw new Exception($"The Path is to long ({buffer.Length + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.DirCreate, buffer, true);

        if(res.Data[0] != 0x00)
            throw new FileTransferException(res.Data[0]);
    }

    public async Task DirDelete(string path)
    {
        byte[] buffer = UTF8Encoding.UTF8.GetBytes(path + char.MinValue);
        if(device.MaxFrameLength < buffer.Length + 2)
            throw new Exception($"The Path is to long ({buffer.Length + 2}) for the MaxAPDU of {device.MaxFrameLength}");

        MsgFunctionPropertyStateRes res = await device.InvokeFunctionProperty(ObjectIndex, (byte)FtmCommands.DirDelete, buffer, true);

        if(res.Data[0] != 0x00)
            throw new FileTransferException(res.Data[0]);
    }
}