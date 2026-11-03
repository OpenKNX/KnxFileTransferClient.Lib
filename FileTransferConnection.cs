using Kaenx.Konnect.Addresses;
using Kaenx.Konnect.Connections;
using Kaenx.Konnect.EMI.DataMessages;
using Kaenx.Konnect.EMI.LData;
using Kaenx.Konnect.Enums;
using Kaenx.Konnect.Telegram.IP;

namespace KnxFileTransferClient.Lib;

/// <summary>
/// Keeps stale unnumbered upload replies from completing the next BusDevice request.
/// Transport acknowledgements remain the underlying connection's responsibility.
/// </summary>
public sealed class FileTransferConnection : IKnxConnection, IDisposable
{
    private readonly IKnxConnection connection;
    private readonly byte[] target;
    private readonly object gate = new();
    private ushort? expectedSequence;

    public event IKnxConnection.ReceivedMessage? OnReceivedMessage;
    public event IKnxConnection.ReceivedService? OnReceivedService;

    public FileTransferConnection(IKnxConnection connection, UnicastAddress target)
    {
        this.connection = connection;
        this.target = target.GetBytes();
        connection.OnReceivedMessage += Receive;
        connection.OnReceivedService += ReceiveService;
    }

    public Task<int> SendAsync(LDataBase message)
    {
        if (message.Content is FunctionPropertyCommand command)
        {
            lock (gate)
            {
                expectedSequence = null;
                if (!message.IsNumbered && command.ObjectIndex == 159 && command.PropertyId == 40 && command.Data.Length >= 3)
                {
                    ushort sequence = (ushort)(command.Data[0] | (command.Data[1] << 8));
                    // Zero opens the file, 0xffff closes it; neither is a data block.
                    if (sequence != 0 && sequence != 0xffff)
                        expectedSequence = sequence;
                }
            }
        }
        return connection.SendAsync(message);
    }

    private void Receive(LDataBase message)
    {
        if (!message.IsNumbered && message.Content is FunctionPropertyStateResponse response &&
            message.SourceAddress.GetBytes().SequenceEqual(target) && IsUploadResponse(message, response) &&
            response.Data.Length >= 5 && response.Data[0] == 0)
        {
            lock (gate)
            {
                if (expectedSequence.HasValue && ((response.Data[1] << 8) | response.Data[2]) < expectedSequence.Value)
                    return;
            }
        }
        // Errors, malformed/future/current replies and other traffic keep their existing handling.
        OnReceivedMessage?.Invoke(message);
    }

    private static bool IsUploadResponse(LDataBase message, FunctionPropertyStateResponse response)
    {
        if (response.ObjectIndex == 159 && response.PropertyId == 40 && response.ReturnCode == ReturnCodes.Success)
            return true;
        // Kaenx currently includes the second APCI byte in decoded function response fields.
        // The actual status byte is then Data[0], checked by Receive.
        return message.RawBytes.Length > 0 && response.ObjectIndex == 0xc9 &&
            response.PropertyId == 159 && (byte)response.ReturnCode == 40;
    }
    private void ReceiveService(IpTelegram telegram) => OnReceivedService?.Invoke(telegram);
    public Task SendAsync(byte[] data) => connection.SendAsync(data);
    public Task Connect() => connection.Connect();
    public Task Disconnect() => connection.Disconnect();
    public bool IsConnected => connection.IsConnected;
    public int GetMaxApduLength() => connection.GetMaxApduLength();
    public UnicastAddress? GetLocalAddress() => connection.GetLocalAddress();

    public void Dispose()
    {
        connection.OnReceivedMessage -= Receive;
        connection.OnReceivedService -= ReceiveService;
    }
}