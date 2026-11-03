using System.Diagnostics;
using Kaenx.Konnect.Addresses;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.Connections;
using Kaenx.Konnect.EMI.DataMessages;
using Kaenx.Konnect.EMI.LData;
using Kaenx.Konnect.Enums;
using Kaenx.Konnect.Exceptions;
using Kaenx.Konnect.Telegram.IP;
using KnxFileTransferClient.Lib;

int failures = 0;
foreach (string mode in new[] { "normal", "duplicate", "older", "resume", "timeout", "future", "crc", "truncated", "short-old-3", "short-old-4", "device-error" })
{
    try {await UploadRegression.Run(mode); Console.WriteLine("PASS: upload " + mode);}
    catch(Exception ex) {failures++; Console.Error.WriteLine("FAIL: upload " + mode + ": " + ex.Message);}
}
try
{
    var wire = new UploadConnection("normal", 1, new byte[122]);
    using var filter = new FileTransferConnection(wire, UnicastAddress.FromString("1.1.1"));
    int received = 0;
    filter.OnReceivedMessage += _ => received++;
    await filter.SendAsync(new LDataBase(UnicastAddress.FromString("1.1.1"), false, 255,
        new FunctionPropertyCommand(159, 40, new byte[] {1, 0, 1, 42})));
    UploadRegression.Check(received == 1, "Current reply missing");
    void Emit(byte[] data, string source, bool numbered = false, uint objectIndex = 159, uint propertyId = 40, ReturnCodes code = ReturnCodes.Success) => wire.Emit(
        new LDataBase(UnicastAddress.FromString("1.1.254"), numbered, numbered ? (byte)0 : (byte)255,
            new FunctionPropertyStateResponse(objectIndex, propertyId, code, data), MessageCodes.L_Data_ind)
            {SourceAddress = UnicastAddress.FromString(source)});
    Emit(new byte[] {0, 0, 0, 0x12, 0x34}, "1.1.1");
    UploadRegression.Check(received == 1, "Old target reply passed");
    Emit(new byte[] {0, 0, 0, 0x12, 0x34}, "1.1.2");
    UploadRegression.Check(received == 2, "Filter changed handling of another device's reply");
    Emit(new byte[] {0, 0, 1, 0x12, 0x34}, "1.1.1");
    UploadRegression.Check(received == 3, "An extra first-response gate is still active");
    Emit(new byte[] {0, 0, 0, 0x12, 0x34}, "1.1.1", true);
    UploadRegression.Check(received == 4, "Numbered reply must reach BusDevice for its ACK");
    int otherBefore = received;
    Emit(new byte[] {0, 0, 0, 0x12, 0x34}, "1.1.1", propertyId: 41);
    UploadRegression.Check(received == otherBefore + 1, "Another function response was filtered");
    Emit(new byte[] {0, 0, 0, 0x12, 0x34}, "1.1.1", objectIndex: 160);
    UploadRegression.Check(received == otherBefore + 2, "Another object response was filtered");
    Emit(new byte[] {0, 0, 0, 0x12, 0x34}, "1.1.1", code: ReturnCodes.Error);
    UploadRegression.Check(received == otherBefore + 3, "Protocol-error response was filtered");
    // Exercise the actual cEMI decoder, including its currently shifted response fields.
    void EmitWire(byte objectIndex, byte propertyId, byte status) => wire.Emit(new LDataBase(
        new byte[] {0x29, 0, 0xb0, 0x60, 0x11, 1, 0x11, 0xfe, 8, 2, 0xc9,
            objectIndex, propertyId, status, 0, 0, 0x12, 0x34}, ExternalMessageInterfaces.cEmi));
    EmitWire(159, 41, 0);
    UploadRegression.Check(received == otherBefore + 4, "Decoded reply for another function was filtered");
    EmitWire(160, 40, 0);
    UploadRegression.Check(received == otherBefore + 5, "Decoded reply for another object was filtered");
    EmitWire(159, 40, 0xff);
    UploadRegression.Check(received == otherBefore + 6, "Decoded error status was filtered");
    await filter.SendAsync(new LDataBase(UnicastAddress.FromString("1.1.1"), false, 255,
        new FunctionPropertyCommand(159, 100, Array.Empty<byte>())));
    int before = received;
    Emit(new byte[] {0, 0, 0, 0x12, 0x34}, "1.1.1");
    UploadRegression.Check(received == before + 1, "Upload filter remained armed for another command");
    Console.WriteLine("PASS: target boundary, current duplicates, numbered replies and reset");
}
catch(Exception ex) {failures++; Console.Error.WriteLine("FAIL: filter boundary: " + ex.Message);}
try {await LoopbackRegression.Run(); Console.WriteLine("PASS: real UDP same/new tunnel sequences and counter wraps");}
catch(Exception ex) {failures++; Console.Error.WriteLine("FAIL: UDP: " + ex.Message);}
Console.WriteLine($"{failures} failure(s)");
return failures == 0 ? 0 : 1;
static class UploadRegression
{
    public static async Task Run(string mode)
    {
        short start = mode == "resume" ? (short)139 : (short)1;
        byte[] file = Enumerable.Range(0, (start + 2) * 122 - 7).Select(i => (byte)(i * 31)).ToArray();
        var wire = new UploadConnection(mode, start, file);
        using var filter = new FileTransferConnection(wire, UnicastAddress.FromString("1.1.1"));
        var device = new BusDevice("1.1.1", filter);
        await device.ConnectIndividual(true);
        device.MaxFrameLength = 255;
        device.SetTimeout(150);
        var client = new FileTransferClient(device);
        var errors = new List<Exception>();
        var messages = new List<string>();
        client.PrintInfo += messages.Add;
        bool expectError = mode is "timeout" or "future" or "crc" or "truncated" or "short-old-3" or "short-old-4";
        client.OnError += ex => { errors.Add(ex); if (expectError) throw new ExpectedFailure(); };
        var clock = Stopwatch.StartNew();
        try
        {
            await client.FileUpload("/fw.bin", file, 128, start, false);
            Check(!expectError && mode != "device-error", "Invalid response accepted as successful upload");
        }
        catch (ExpectedFailure) when (expectError) { }
        catch (Exception ex) when (mode == "device-error" && ex.InnerException is FileTransferException) { }
        await wire.Drain();
        if (expectError)
        {
            Check(errors.Count == 1, "Expected one observable transfer error");
            Check(mode switch {
                "timeout" => errors[0] is TimeoutException,
                "future" or "short-old-3" or "short-old-4" => errors[0] is SequenceMissmatchException,
                "crc" => errors[0].Message.Contains("CRC"),
                _ => errors[0] is IndexOutOfRangeException
            }, "Wrong error: " + errors[0].GetType().Name + " " + errors[0].Message);
        }
        else if (mode != "device-error")
        {
            Check(errors.Count == 0, $"Old reply triggered {errors.Count} transfer error(s)");
            Check(!messages.Any(m => m.Contains("Warte 3s")), "Old reply triggered three-second backoff");
            Check(wire.Attempts.Values.All(n => n == 1), "Data block was unnecessarily resent");
            Check(wire.Stored.SequenceEqual(file), "Uploaded bytes differ");
            Check(clock.Elapsed < TimeSpan.FromSeconds(2), "Upload incurred unexpected backoff");
        }
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class ExpectedFailure : Exception { }
}

sealed class UploadConnection : IKnxConnection
{
    public event IKnxConnection.ReceivedMessage? OnReceivedMessage;
    public event IKnxConnection.ReceivedService? OnReceivedService { add { } remove { } }
    readonly string mode;
    readonly short start;
    readonly List<Task> pending = new();
    readonly Dictionary<ushort, byte[]> answers = new();
    public Dictionary<ushort, int> Attempts { get; } = new();
    public List<(ushort Sequence, long Ticks)> DataSends { get; } = new();
    public Dictionary<ushort, long> ReplyTicks { get; } = new();
    public byte[] Stored { get; }
    public UploadConnection(string mode, short start, byte[] file)
    {
        this.mode = mode; this.start = start; Stored = new byte[file.Length];
        Array.Copy(file, Stored, (start - 1) * 122);
    }
    public Task<int> SendAsync(LDataBase message)
    {
        if (message.Content is not FunctionPropertyCommand request) throw new Exception("Unexpected request");
        if (request.PropertyId == 102) { Reply(102, new byte[] { 1 }); return Task.FromResult(0); }
        if (request.PropertyId == 100) { Reply(100, new byte[] { 0, 0, 0, 1, 0, 7 }); return Task.FromResult(0); }
        if (request.PropertyId != 40) throw new Exception("Unexpected property");
        ushort seq = BitConverter.ToUInt16(request.Data);
        if (seq == 0 || seq == 0xffff) { Reply(40, new byte[] { 0 }); return Task.FromResult(0); }
        Attempts.TryGetValue(seq, out int attempts); Attempts[seq] = ++attempts;
        DataSends.Add((seq, Stopwatch.GetTimestamp()));
        Array.Copy(request.Data, 3, Stored, (seq - 1) * 122, request.Data[2]);
        int crc = CRC16.Get(request.Data);
        byte[] answer = { 0, (byte)(seq >> 8), (byte)seq, (byte)(crc >> 8), (byte)crc };
        bool inject = attempts == 1 && (mode is "resume" or "timeout" or "future" or "crc" or "truncated" or "short-old-3" or "short-old-4" or "device-error" || seq > start && mode is "duplicate" or "older");
        if (mode == "numbered")
        {
            Reply(40, answer, numbered: true);
            Reply(40, answer, numbered: true);
        }
        else if (inject)
        {
            ushort old = mode == "older" ? (ushort)start : (ushort)(seq - 1);
            byte[] prior = answers.TryGetValue(old, out var known) ? known : new byte[] { 0, (byte)(old >> 8), (byte)old, 0x12, 0x34 };
            byte[] injected = mode switch {
                "future" => new byte[] { 0, (byte)((seq + 1) >> 8), (byte)(seq + 1), 0, 0 },
                "crc" => new byte[] { 0, (byte)(seq >> 8), (byte)seq, (byte)((crc >> 8) ^ 1), (byte)crc },
                "truncated" => new byte[] { 0 },
                "short-old-3" => new byte[] { 0, 0, 0 },
                "short-old-4" => new byte[] { 0, 0, 0, 0 },
                "device-error" => new byte[] { 0x47 },
                _ => prior
            };
            Reply(40, injected);
            if (mode is "duplicate" or "older" or "resume")
                pending.Add(ReplyLater(answer));
        }
        else Reply(40, answer);
        answers[seq] = answer;
        return Task.FromResult(0);
    }
    async Task ReplyLater(byte[] answer) { await Task.Delay(25); Reply(40, answer); }
    void Reply(uint property, byte[] data, string source = "1.1.1", bool numbered = false)
    {
        if (property == 40 && data.Length >= 5 && data[0] == 0)
            ReplyTicks[(ushort)((data[1] << 8) | data[2])] = Stopwatch.GetTimestamp();
        var response = new FunctionPropertyStateResponse(159, property, ReturnCodes.Success, data);
        OnReceivedMessage?.Invoke(new LDataBase(UnicastAddress.FromString("1.1.254"), numbered, numbered ? (byte)0 : (byte)255, response, MessageCodes.L_Data_ind) { SourceAddress = UnicastAddress.FromString(source) });
    }
    public void Emit(LDataBase message) => OnReceivedMessage?.Invoke(message);
    public Task Drain() => Task.WhenAll(pending);
    public Task SendAsync(byte[] data) => throw new System.NotSupportedException();
    public Task Connect() => Task.CompletedTask;
    public Task Disconnect() => Task.CompletedTask;
    public bool IsConnected => true;
    public int GetMaxApduLength() => 255;
    public UnicastAddress? GetLocalAddress() => UnicastAddress.FromString("1.1.254");
}
