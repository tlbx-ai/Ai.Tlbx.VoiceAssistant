using System.Buffers.Binary;

namespace Ai.Tlbx.VoiceAssistant.Telnyx;

/// <summary>Telnyx WebSocket payloads contain raw codec samples, without RTP or WAV headers.</summary>
public static class TelnyxPcmCodec
{
    public static byte[] Decode(ReadOnlySpan<byte> audio, string encoding)
    {
        if (encoding == "L16")
        {
            if ((audio.Length & 1) != 0) throw new ArgumentException("L16 payload must contain complete PCM16 samples.");
            // Telnyx's WebSocket L16 is little endian (see the official realtime-ai-demo wire diagnostics).
            return audio.ToArray();
        }
        if (encoding is not ("PCMU" or "PCMA")) throw new NotSupportedException($"The PCM adapter cannot decode {encoding}.");
        byte[] result = new byte[audio.Length * 2];
        for (int i = 0; i < audio.Length; i++)
        {
            int value;
            if (encoding == "PCMU")
            {
                int sample = (~audio[i]) & 255;
                int magnitude = (((sample & 15) << 3) + 132) << ((sample >> 4) & 7);
                value = (sample & 128) != 0 ? 132 - magnitude : magnitude - 132;
            }
            else
            {
                int sample = audio[i] ^ 0x55, segment = (sample >> 4) & 7;
                int magnitude = ((sample & 15) << 4) + (segment == 0 ? 8 : 0x108);
                if (segment > 1) magnitude <<= segment - 1;
                value = (sample & 128) != 0 ? magnitude : -magnitude;
            }
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), (short)value);
        }
        return result;
    }

    public static byte[] Encode(ReadOnlySpan<byte> pcm16, string encoding)
    {
        if ((pcm16.Length & 1) != 0) throw new ArgumentException("PCM16 payload must contain complete samples.");
        if (encoding == "L16") return pcm16.ToArray();
        if (encoding is not ("PCMU" or "PCMA")) throw new NotSupportedException($"The PCM adapter cannot encode {encoding}.");
        byte[] result = new byte[pcm16.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            int value = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(i * 2)..]);
            if (encoding == "PCMU")
            {
                int sign = value < 0 ? 128 : 0;
                value = Math.Min(Math.Abs(value), 32635) + 132;
                int exponent = 7;
                for (int mask = 0x4000; exponent > 0 && (value & mask) == 0; exponent--, mask >>= 1) { }
                result[i] = (byte)~(sign | exponent << 4 | ((value >> (exponent + 3)) & 15));
            }
            else
            {
                int mask = value >= 0 ? 0xD5 : 0x55;
                value = Math.Min(value < 0 ? -value - 1 : value, 32767);
                int segment = 0;
                for (int end = 255; segment < 7 && value > end; segment++, end = (end << 1) | 1) { }
                result[i] = (byte)(((segment << 4) | ((value >> (segment == 0 ? 4 : segment + 3)) & 15)) ^ mask);
            }
        }
        return result;
    }
}

/// <summary>Stateful windowed-sinc resampling. Keeps phase/filter history across provider/audio packet boundaries.</summary>
internal sealed class Pcm16Resampler(int sourceRate, int targetRate)
{
    private const int Radius = 16;
    private readonly List<short> _samples = [];
    private long _offset;
    private long _total;
    private double _position;
    private long _outputSamples;

    public byte[] Process(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        if (sourceRate < 8000 || targetRate < 8000 || sourceRate > 192000 || targetRate > 192000)
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if ((bytes.Length & 1) != 0) throw new ArgumentException("PCM16 must be sample aligned.");
        if (sourceRate == targetRate) return bytes.ToArray();
        for (int i = 0; i < bytes.Length; i += 2) _samples.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes[i..]));
        _total += bytes.Length / 2;
        var output = new List<short>();
        double cutoff = Math.Min(1.0, (double)targetRate / sourceRate) * 0.94;
        while (_outputSamples * sourceRate < (flush ? _total : _total - Radius) * targetRate)
        {
            _position = _outputSamples * (double)sourceRate / targetRate;
            long center = (long)Math.Floor(_position);
            double sum = 0, weight = 0;
            for (long index = center - Radius + 1; index <= center + Radius; index++)
            {
                double distance = index - _position;
                double x = Math.PI * distance * cutoff;
                double tap = (Math.Abs(x) < 1e-10 ? 1 : Math.Sin(x) / x) * (0.5 + 0.5 * Math.Cos(Math.PI * distance / Radius));
                long actual = Math.Clamp(index, _offset, _total - 1);
                sum += _samples[(int)(actual - _offset)] * tap;
                weight += tap;
            }
            output.Add((short)Math.Clamp(Math.Round(sum / weight), short.MinValue, short.MaxValue));
            _outputSamples++;
        }
        _position = _outputSamples * (double)sourceRate / targetRate;
        int discard = (int)Math.Min(_samples.Count, Math.Max(0, (long)Math.Floor(_position) - Radius - _offset));
        _samples.RemoveRange(0, discard); _offset += discard;
        var result = new byte[output.Count * 2];
        for (int i = 0; i < output.Count; i++) BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), output[i]);
        return result;
    }
}
