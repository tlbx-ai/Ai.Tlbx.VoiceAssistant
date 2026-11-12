using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Independent, deliberately small SIP/PCMA peer. Cannot route to real phone networks.
internal sealed class LoopbackPbx : IAsyncDisposable
{
    private readonly UdpClient _sip = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _rtp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _rtcp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop, _media;
    private IPEndPoint? _peer;
    private Dictionary<string, string>? _invite;
    public bool HoldAnswer { get; init; }
    public bool Reject { get; init; }
    public int Port => ((IPEndPoint)_sip.Client.LocalEndPoint!).Port;
    public int ReceivedAudio, Cancelled, HungUp, Unregistered;
    public System.Collections.Concurrent.ConcurrentQueue<byte[]> ReceivedPayloads { get; } = new();
    public TaskCompletionSource Invited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public LoopbackPbx() { _loop = RunAsync(); _media = ReceiveAudioAsync(); }
    private async Task ReceiveAudioAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var packet = await _rtp.ReceiveAsync(_stop.Token);
                    if (packet.Buffer.Length > 12) ReceivedPayloads.Enqueue(packet.Buffer[12..]);
                    if (packet.Buffer.Length > 12 && packet.Buffer.Skip(12).Any(b => b != 0xd5)) Interlocked.Increment(ref ReceivedAudio);
                }
                catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _sip.ReceiveAsync(_stop.Token); _peer = packet.RemoteEndPoint;
                string text = Encoding.UTF8.GetString(packet.Buffer);
                var lines = text.Split("\r\n");
                if (lines[0].StartsWith("SIP/")) continue;
                var h = lines.Skip(1).TakeWhile(l => l.Length > 0).Select(l => l.Split(':', 2))
                    .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                string method = lines[0].Split(' ')[0];
                if (method == "ACK") continue;
                if (method == "REGISTER" && h.GetValueOrDefault("Expires") == "0") Interlocked.Increment(ref Unregistered);
                if (method == "BYE") Interlocked.Increment(ref HungUp);
                if (method == "INVITE")
                {
                    _invite = h; Invited.TrySetResult(); await ReplyAsync(h, 180, "Ringing");
                    if (HoldAnswer) continue;
                    if (Reject) { await ReplyAsync(h, 486, "Busy Here"); continue; }
                    int peerRtp = int.Parse(text.Split("m=audio ")[1].Split(' ')[0]);
                    int ownRtp = ((IPEndPoint)_rtp.Client.LocalEndPoint!).Port;
                    int ownRtcp = ((IPEndPoint)_rtcp.Client.LocalEndPoint!).Port;
                    string sdp = $"v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {ownRtp} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\na=rtcp:{ownRtcp} IN IP4 127.0.0.1\r\na=sendrecv\r\n";
                    await ReplyAsync(h, 200, "OK", sdp);
                    // Paced input proves resampling without a physical speaker or microphone.
                    for (ushort i = 0; i < 12; i++)
                    {
                        byte[] audio = new byte[172]; audio[0] = 0x80; audio[1] = 8;
                        BinaryPrimitives.WriteUInt16BigEndian(audio.AsSpan(2), i);
                        BinaryPrimitives.WriteUInt32BigEndian(audio.AsSpan(4), (uint)i * 160);
                        BinaryPrimitives.WriteUInt32BigEndian(audio.AsSpan(8), 42); audio.AsSpan(12).Fill(0x80);
                        await _rtp.SendAsync(audio, new IPEndPoint(IPAddress.Loopback, peerRtp), _stop.Token);
                        await Task.Delay(20, _stop.Token);
                    }
                    continue;
                }
                await ReplyAsync(h, 200, "OK");
                if (method == "CANCEL")
                {
                    Interlocked.Increment(ref Cancelled); await ReplyAsync(_invite!, 487, "Request Terminated");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private Task ReplyAsync(Dictionary<string, string> h, int code, string reason, string body = "")
    {
        string to = h["To"].Contains(";tag=") ? h["To"] : h["To"] + ";tag=test-peer";
        string contact = h["CSeq"].EndsWith("REGISTER") ? h["Contact"] + ";expires=" + h.GetValueOrDefault("Expires", "300") : $"<sip:peer@127.0.0.1:{Port}>";
        string text = $"SIP/2.0 {code} {reason}\r\nVia: {h["Via"]}\r\nFrom: {h["From"]}\r\nTo: {to}\r\nCall-ID: {h["Call-ID"]}\r\nCSeq: {h["CSeq"]}\r\nContact: {contact}\r\n";
        if (body.Length > 0) text += "Content-Type: application/sdp\r\n";
        return SendAsync(text + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
    }
    private async Task SendAsync(string text) => await _sip.SendAsync(Encoding.UTF8.GetBytes(text), _peer!, _stop.Token);
    public Task RemoteHangupAsync()
    {
        var h = _invite!;
        return SendAsync($"BYE sip:demo@{_peer!.Address}:{_peer.Port} SIP/2.0\r\nVia: SIP/2.0/UDP 127.0.0.1:{Port};branch=z9hG4bKremote-bye\r\nFrom: {h["To"]};tag=test-peer\r\nTo: {h["From"]}\r\nCall-ID: {h["Call-ID"]}\r\nCSeq: 2 BYE\r\nContent-Length: 0\r\n\r\n");
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await Task.WhenAll(_loop, _media);
        _sip.Dispose(); _rtp.Dispose(); _rtcp.Dispose(); _stop.Dispose();
    }
}
