using System.Diagnostics;
using Kaenx.Konnect.Addresses;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.Connections;
using Kaenx.Konnect.EMI.DataMessages;
using Kaenx.Konnect.EMI.LData;
using Kaenx.Konnect.Enums;
using Kaenx.Konnect.Telegram.IP;
using KnxFileTransferClient.Lib;

try
{
    foreach (int? delay in new int?[] { 60, 0, null })
    {
        byte[] file=Enumerable.Range(0,359).Select(i=>(byte)i).ToArray();
        var wire=new UploadConnection(file.Length);
        var device=new BusDevice("1.1.1",wire);
        await device.ConnectIndividual(true);
        device.MaxFrameLength=255;
        var client=delay.HasValue ? new FileTransferClient(device,delay.Value) : new FileTransferClient(device);
        client.OnError+=ex=>throw new Exception("Unexpected upload error",ex);
        await client.FileUpload("/fw.bin",file,128,1,false);
        Check(wire.Stored.SequenceEqual(file),"Pacing changed file bytes");
        Check(wire.Sends.Count==3,"Pacing changed send count");
        if(delay>0)
            foreach(var sent in wire.Sends.Skip(1))
            {
                double gap=Stopwatch.GetElapsedTime(wire.Replies[(ushort)(sent.Seq-1)],sent.Ticks).TotalMilliseconds;
                Check(gap>=50,$"Configured 60 ms pause missing: observed {gap:F2} ms");
            }
        Console.WriteLine($"PASS: upload delay={delay?.ToString() ?? "legacy-default"}, payload and send count");
    }
    var negativeConstructor=typeof(FileTransferClient).GetConstructor(new[] {typeof(BusDevice),typeof(int)})!;
    try
    {
        _ = new FileTransferClient(new BusDevice("1.1.1",new UploadConnection(0)),-1);
        throw new Exception("Negative delay accepted");
    }
    catch(ArgumentOutOfRangeException) { }
    Console.WriteLine("PASS: negative delay rejected");
    return 0;
}
catch(Exception ex) {Console.Error.WriteLine("FAIL: "+ex.Message); return 1;}

static void Check(bool condition,string message) {if(!condition) throw new Exception(message);}

sealed class UploadConnection : IKnxConnection
{
    public event IKnxConnection.ReceivedMessage? OnReceivedMessage;
    public event IKnxConnection.ReceivedService? OnReceivedService {add{} remove{}}
    public byte[] Stored {get;}
    public List<(ushort Seq,long Ticks)> Sends {get;}=new();
    public Dictionary<ushort,long> Replies {get;}=new();
    public UploadConnection(int length) {Stored=new byte[length];}
    public async Task<int> SendAsync(LDataBase message)
    {
        var request=(FunctionPropertyCommand)message.Content!;
        byte[] answer;
        ushort seq=0;
        if(request.PropertyId==102) answer=new byte[] {1};
        else if(request.PropertyId==100) answer=new byte[] {0,0,0,1,0,7};
        else if(request.PropertyId==40)
        {
            seq=BitConverter.ToUInt16(request.Data);
            if(seq is 0 or 0xffff) answer=new byte[] {0};
            else
            {
                Sends.Add((seq,Stopwatch.GetTimestamp()));
                Array.Copy(request.Data,3,Stored,(seq-1)*122,request.Data[2]);
                int crc=CRC16.Get(request.Data);
                answer=new byte[] {0,(byte)(seq>>8),(byte)seq,(byte)(crc>>8),(byte)crc};
            }
        }
        else throw new Exception("Unexpected property");
        await Task.Delay(2);
        if(seq is >0 and <0xffff) Replies[seq]=Stopwatch.GetTimestamp();
        OnReceivedMessage?.Invoke(new LDataBase(UnicastAddress.FromString("1.1.254"),false,255,
            new FunctionPropertyStateResponse(159,request.PropertyId,ReturnCodes.Success,answer),MessageCodes.L_Data_ind)
            {SourceAddress=UnicastAddress.FromString("1.1.1")});
        return 0;
    }
    public Task SendAsync(byte[] data)=>throw new NotSupportedException();
    public Task Connect()=>Task.CompletedTask;
    public Task Disconnect()=>Task.CompletedTask;
    public bool IsConnected=>true;
    public int GetMaxApduLength()=>255;
    public UnicastAddress? GetLocalAddress()=>UnicastAddress.FromString("1.1.254");
}