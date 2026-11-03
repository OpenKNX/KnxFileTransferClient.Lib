using System.Net;
using System.Net.Sockets;
using Kaenx.Konnect;
using Kaenx.Konnect.Classes;
using Kaenx.Konnect.EMI.DataMessages;
using Kaenx.Konnect.Addresses;
using KnxFileTransferClient.Lib;

// No real KNX bus is used. A loopback gateway injects one previous response
// immediately before the current reply; all production receiver code is real.
static class LoopbackRegression
{
public static async Task Run()
{
int failures = 0;
foreach (string scenario in new[] { "normal", "same-tunnel-sequence", "new-tunnel-sequence", "long-wrap" })
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var gateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var connection = KnxFactory.CreateTunnelingUdp((IPEndPoint)gateway.Client.LocalEndPoint!);
    var serve = Serve(gateway, scenario, stop.Token);
    try
    {
        await connection.Connect();
        using var filter = new FileTransferConnection(connection, UnicastAddress.FromString("1.1.1"));
        var device = new BusDevice("1.1.1", filter);
        await device.ConnectIndividual(true);
        var first = await device.InvokeFunctionProperty(159, 40, new byte[] { 0x8a, 0, 1, 42 });
        int firstSequence = (first.Data[1] << 8) | first.Data[2];
        if (firstSequence != 0x8a) throw new Exception($"Invalid initial response {firstSequence:X4}");
        var second = await device.InvokeFunctionProperty(159, 40, new byte[] { 0x8b, 0, 1, 43 });
        int secondSequence = (second.Data[1] << 8) | second.Data[2];
        bool passed = secondSequence == 0x8b;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {scenario}: Req 008B / Res {secondSequence:X4}");
        if (!passed) failures++;
        if (scenario == "long-wrap")
        {
            for (ushort sequence = 0x8c; sequence < 0x8c + 300; sequence++)
            {
                var reply = await device.InvokeFunctionProperty(159, 40, new byte[] { (byte)sequence, (byte)(sequence >> 8), 1, 42 });
                if (((reply.Data[1] << 8) | reply.Data[2]) != sequence)
                    throw new Exception($"Incorrect response after counter wrap at {sequence:X4}");
            }
        }
        await Task.Delay(100);
        await connection.Disconnect();
    }
    finally
    {
        connection.Dispose();
        stop.Cancel();
        try { await serve; } catch (OperationCanceledException) { }
    }
}
Console.WriteLine($"{failures} stale replies incorrectly accepted; no DataSecure telegrams were sent.");
if (failures != 0) throw new Exception($"{failures} old replies reached the next request");

}
static async Task Serve(UdpClient gateway, string scenario, CancellationToken stop)
{
    byte counter = 0;
    byte[]? previousReply = null;
    int requests = 0;
    while (!stop.IsCancellationRequested)
    {
        var received = await gateway.ReceiveAsync(stop);
        byte[] packet = received.Buffer;
        int service = (packet[2] << 8) | packet[3];
        if (service == 0x0205)
        {
            var local = (IPEndPoint)gateway.Client.LocalEndPoint!;
            await gateway.SendAsync(new byte[] { 6, 0x10, 2, 6, 0, 20, 0x21, 0, 8, 1, 127, 0, 0, 1, (byte)(local.Port >> 8), (byte)local.Port, 4, 4, 0x11, 0xfe }, received.RemoteEndPoint, stop);
        }
        else if (service == 0x0420)
        {
            await gateway.SendAsync(new byte[] { 6, 0x10, 4, 0x21, 0, 10, 4, 0x21, packet[8], 0 }, received.RemoteEndPoint, stop);
            var confirmation = packet.ToArray();
            // Confirmation counter correlation is tested in Kaenx separately.
            // Echo it here so this filter regression also runs without that fix.
            confirmation[8] = packet[8];
            confirmation[10] = 0x2e;
            await gateway.SendAsync(confirmation, received.RemoteEndPoint, stop);
            if (packet.Length < 25 || packet[20] != 0xc7) continue;
            byte sequenceLow = packet[23];
            byte sequenceHigh = packet[24];
            requests++;
            if (requests == 2 && scenario != "normal" || scenario == "long-wrap" && requests % 7 == 0)
            {
                var duplicate = previousReply!.ToArray();
                if (scenario is "new-tunnel-sequence" or "long-wrap") duplicate[8] = counter++;
                if (scenario != "long-wrap") Console.WriteLine($"INJECT: {scenario}, tunnel {duplicate[8]}, old file sequence 008A before current 008B");
                await gateway.SendAsync(duplicate, received.RemoteEndPoint, stop);
                await Task.Delay(50, stop);
            }
            // cEMI L_Data_ind, unnumbered FunctionPropertyStateResponse (0x2c9),
            // object 159/property 40, result OK + big-endian file sequence + CRC.
            byte[] reply = { 6, 0x10, 4, 0x20, 0, 28, 4, 0x21, counter++, 0,
                0x29, 0, 0xb0, 0x60, 0x11, 1, 0x11, 0xfe, 8, 2, 0xc9,
                159, 40, 0, sequenceHigh, sequenceLow, 0x12, 0x34 };
            await gateway.SendAsync(reply, received.RemoteEndPoint, stop);
            previousReply = reply;
        }
    }
}

}